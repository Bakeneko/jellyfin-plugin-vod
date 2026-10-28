using System;
using System.Collections.Generic;
using Jellyfin.Plugin.VOD.Services;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class TsSegmentFilterTests
    {
        // ── helpers ───────────────────────────────────────────────────────────

        private const int VideoPid = 0x100;
        private const int PmtPid = 0x20;

        /// <summary>
        /// Builds a minimal 3-packet MPEG-TS: PAT → PMT → one video PES containing
        /// <paramref name="h264Payload"/> as H.264 Annex-B data.
        /// </summary>
        private static byte[] BuildMinimalTs(byte[] h264Payload)
        {
            var packets = new List<byte[]> { BuildPat(), BuildPmt() };

            // PES header: start code + stream_id + length + flags + header_data_length + PTS (5 bytes)
            var pesHeader = new byte[]
            {
                0x00, 0x00, 0x01, 0xE0,  // start code + video stream ID
                0x00, 0x00,              // PES packet length (0 = unbounded)
                0x10,                    // marker bits etc.
                0xA1,                    // PTS_DTS_flags=10
                0x05,                    // header_data_length = 5
                0x21, 0x00, 0x01, 0x00, 0x01  // dummy PTS
            };

            var pesPayload = new byte[pesHeader.Length + h264Payload.Length];
            Buffer.BlockCopy(pesHeader, 0, pesPayload, 0, pesHeader.Length);
            Buffer.BlockCopy(h264Payload, 0, pesPayload, pesHeader.Length, h264Payload.Length);

            // Split PES payload into 184-byte TS chunks
            var firstChunk = true;
            for (var pos = 0; pos < pesPayload.Length; pos += 184)
            {
                var pkt = new byte[188];
                pkt[0] = 0x47;
                pkt[1] = (byte)((firstChunk ? 0x40 : 0x00) | (VideoPid >> 8));
                pkt[2] = (byte)(VideoPid & 0xFF);
                pkt[3] = 0x10; // payload-only, CC=0

                var copy = Math.Min(184, pesPayload.Length - pos);
                Buffer.BlockCopy(pesPayload, pos, pkt, 4, copy);
                // Fill unused bytes with 0xFF padding
                for (var i = 4 + copy; i < 188; i++) pkt[i] = 0xFF;

                packets.Add(pkt);
                firstChunk = false;
            }

            var result = new byte[packets.Count * 188];
            for (var i = 0; i < packets.Count; i++)
                Buffer.BlockCopy(packets[i], 0, result, i * 188, 188);
            return result;
        }

        private static byte[] BuildPat()
        {
            // PAT: table_id=0x00, section_length=13, one program pointing to PMT
            var pkt = new byte[188];
            pkt[0] = 0x47;
            pkt[1] = 0x40; pkt[2] = 0x00; // PUSI=1, PID=0
            pkt[3] = 0x10;                 // payload-only
            pkt[4] = 0x00;                 // pointer_field
            pkt[5] = 0x00;                 // table_id = PAT
            pkt[6] = 0xB0; pkt[7] = 0x0D; // section_length = 13
            pkt[8] = 0x00; pkt[9] = 0x01;  // transport_stream_id
            pkt[10] = 0xC1;                 // version=0, current_next=1
            pkt[11] = 0x00;                 // section_number
            pkt[12] = 0x00;                 // last_section_number
            pkt[13] = 0x00; pkt[14] = 0x01; // program_number = 1
            pkt[15] = (byte)(0xE0 | (PmtPid >> 8));
            pkt[16] = (byte)(PmtPid & 0xFF);
            // CRC (not validated by parser, zeroed)
            for (var i = 21; i < 188; i++) pkt[i] = 0xFF;
            return pkt;
        }

        private static byte[] BuildPmt()
        {
            // PMT: table_id=0x02, section_length=18, one H.264 ES
            var pkt = new byte[188];
            pkt[0] = 0x47;
            pkt[1] = (byte)(0x40 | (PmtPid >> 8));
            pkt[2] = (byte)(PmtPid & 0xFF);
            pkt[3] = 0x10;
            pkt[4] = 0x00;                 // pointer_field
            pkt[5] = 0x02;                 // table_id = PMT
            pkt[6] = 0xB0; pkt[7] = 0x12; // section_length = 18
            pkt[8] = 0x00; pkt[9] = 0x01;  // program_number
            pkt[10] = 0xC1;
            pkt[11] = 0x00;                 // section_number
            pkt[12] = 0x00;                 // last_section_number
            pkt[13] = (byte)(0xE0 | (VideoPid >> 8));
            pkt[14] = (byte)(VideoPid & 0xFF); // PCR_PID
            pkt[15] = 0xF0; pkt[16] = 0x00;   // program_info_length = 0
            pkt[17] = 0x1B;                    // stream_type = H.264
            pkt[18] = (byte)(0xE0 | (VideoPid >> 8));
            pkt[19] = (byte)(VideoPid & 0xFF); // elementary_PID
            pkt[20] = 0xF0; pkt[21] = 0x00;   // ES_info_length = 0
            // CRC (not validated by parser, zeroed)
            for (var i = 26; i < 188; i++) pkt[i] = 0xFF;
            return pkt;
        }

        private static int FindByteSequence(byte[] data, byte[] pattern)
        {
            for (var i = 0; i <= data.Length - pattern.Length; i++)
            {
                var match = true;
                for (var j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j]) { match = false; break; }
                }

                if (match) return i;
            }

            return -1;
        }

        // ── tests ─────────────────────────────────────────────────────────────

        [Fact]
        public void StripTrailingSeis_TrailingSeiAfterVcl_IsReplacedWithFillerData()
        {
            // YouTube-style: IDR (VCL) followed by a trailing SEI
            var h264 = new byte[]
            {
                0x00, 0x00, 0x00, 0x01, 0x65, 0x00, 0x00, // IDR slice (VCL type=5)
                0x00, 0x00, 0x00, 0x01, 0x06, 0x05, 0x01, 0x80  // trailing SEI (type=6)
            };

            var ts = BuildMinimalTs(h264);
            var seiOffset = FindByteSequence(ts, [0x00, 0x00, 0x00, 0x01, 0x06]);
            Assert.True(seiOffset >= 0, "SEI start code should exist in the original TS");

            var result = TsSegmentFilter.StripTrailingSeis(ts);

            // Start code preserved
            Assert.Equal(0x00, result[seiOffset]);
            Assert.Equal(0x00, result[seiOffset + 1]);
            Assert.Equal(0x00, result[seiOffset + 2]);
            Assert.Equal(0x01, result[seiOffset + 3]);
            // NAL type: SEI (6) → filler_data (12)
            Assert.Equal(0x0C, result[seiOffset + 4]);
            // Content bytes become 0xFF filler (no next start code → fills to end of TS chunk)
            Assert.Equal(0xFF, result[seiOffset + 5]);
            Assert.Equal(0xFF, result[seiOffset + 6]);
            // The original SEI trailing byte (0x80) is now 0xFF too, because nalEnd = payload.Length;
            // 0x80 lands at the very last byte of the 184-byte TS chunk, not at seiOffset+7
            Assert.Equal(0xFF, result[seiOffset + 7]);
        }

        [Fact]
        public void StripTrailingSeis_ConformantSeiBeforeVcl_IsNotModified()
        {
            // Conformant stream: SEI before VCL → must not be touched
            var h264 = new byte[]
            {
                0x00, 0x00, 0x00, 0x01, 0x06, 0x05, 0x01, 0x80, // SEI before IDR (conformant)
                0x00, 0x00, 0x00, 0x01, 0x65, 0x00, 0x00          // IDR slice (VCL)
            };

            var ts = BuildMinimalTs(h264);
            var seiOffset = FindByteSequence(ts, [0x00, 0x00, 0x00, 0x01, 0x06]);
            Assert.True(seiOffset >= 0);

            var result = TsSegmentFilter.StripTrailingSeis(ts);

            // NAL type must remain 0x06
            Assert.Equal(0x06, result[seiOffset + 4]);
        }

        [Fact]
        public void StripTrailingSeis_NoSei_ReturnsBytewiseIdenticalData()
        {
            // No SEI at all: result must be a byte-for-byte clone
            var h264 = new byte[]
            {
                0x00, 0x00, 0x00, 0x01, 0x65, 0x00, 0x00  // IDR only
            };

            var ts = BuildMinimalTs(h264);
            var result = TsSegmentFilter.StripTrailingSeis(ts);

            Assert.Equal(ts.Length, result.Length);
            Assert.Equal<byte>(ts, result);
        }

        [Fact]
        public void StripTrailingSeis_OutputLength_EqualInputLength()
        {
            // Verify in-place replacement never changes the buffer length
            var h264 = new byte[]
            {
                0x00, 0x00, 0x00, 0x01, 0x65, 0x00,
                0x00, 0x00, 0x00, 0x01, 0x06, 0x00, 0x80
            };

            var ts = BuildMinimalTs(h264);
            var result = TsSegmentFilter.StripTrailingSeis(ts);

            Assert.Equal(ts.Length, result.Length);
        }

        [Fact]
        public void StripTrailingSeis_EmptyInput_DoesNotThrow()
        {
            var result = TsSegmentFilter.StripTrailingSeis([]);
            Assert.Empty(result);
        }

        [Fact]
        public void StripTrailingSeis_InputShorterThanTsPacket_DoesNotThrow()
        {
            var result = TsSegmentFilter.StripTrailingSeis(new byte[100]);
            Assert.Equal(100, result.Length);
        }
    }
}
