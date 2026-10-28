using System.Collections.Generic;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>
    /// Repairs non-conformant H.264 MPEG-TS segments that carry SEI NAL units
    /// after the VCL NAL unit of the same access unit.
    /// </summary>
    /// <remarks>
    /// Per H.264 spec, SEI must precede VCL NALs.  YouTube encodes some videos
    /// with trailing SEIs.  When ffmpeg remuxes such a stream in copy mode it
    /// emits the trailing SEI as a separate AVPacket without PTS/DTS, and
    /// ExoPlayer (Media3 &lt;1.9.0) crashes with an IllegalArgumentException in
    /// ReorderingBufferQueue.  We replace every trailing SEI with a filler_data
    /// NAL unit (type 12) of identical byte length, which leaves all TS packet
    /// boundaries intact and produces a spec-conformant stream.
    /// </remarks>
    internal static class TsSegmentFilter
    {
        private const int TsPacketSize = 188;
        private const byte TsSyncByte = 0x47;
        private const byte H264StreamType = 0x1B;

        /// <summary>
        /// Returns a copy of <paramref name="data"/> with any trailing SEI NAL
        /// units (H.264 type 6) that follow a VCL NAL replaced by filler data.
        /// Returns the original array unchanged when the segment carries no
        /// H.264 video stream.
        /// </summary>
        public static byte[] StripTrailingSeis(byte[] data)
        {
            var result = (byte[])data.Clone();
            var videoPid = FindVideoPid(result);
            if (videoPid >= 0)
            {
                ProcessVideoPackets(result, videoPid);
            }

            return result;
        }

        // ── TS/PMT parsing ────────────────────────────────────────────────────

        private static int FindVideoPid(byte[] data)
        {
            var pmtPid = -1;

            // Pass 1: find PMT PID from PAT (PID 0)
            for (var i = 0; i + TsPacketSize <= data.Length; i += TsPacketSize)
            {
                if (data[i] != TsSyncByte) continue;
                if (GetPid(data, i) != 0) continue;
                if ((data[i + 1] & 0x40) == 0) continue; // require PUSI

                var ps = GetPayloadStart(data, i);
                if (ps < 0) continue;

                // pointer_field then table
                var off = ps + 1 + data[ps];
                if (off + 8 > data.Length || data[off] != 0x00) continue;

                var secLen = ((data[off + 1] & 0x0F) << 8) | data[off + 2];
                var end = off + 3 + secLen - 4; // exclude CRC
                off += 8; // skip fixed PAT header

                while (off + 4 <= end)
                {
                    var progNum = (data[off] << 8) | data[off + 1];
                    if (progNum != 0)
                    {
                        pmtPid = ((data[off + 2] & 0x1F) << 8) | data[off + 3];
                        break;
                    }

                    off += 4;
                }

                if (pmtPid >= 0) break;
            }

            if (pmtPid < 0) return -1;

            // Pass 2: find H.264 PID from PMT
            for (var i = 0; i + TsPacketSize <= data.Length; i += TsPacketSize)
            {
                if (data[i] != TsSyncByte) continue;
                if (GetPid(data, i) != pmtPid) continue;
                if ((data[i + 1] & 0x40) == 0) continue; // require PUSI

                var ps = GetPayloadStart(data, i);
                if (ps < 0) continue;

                var off = ps + 1 + data[ps]; // skip pointer_field
                if (off + 12 > data.Length || data[off] != 0x02) continue;

                var secLen = ((data[off + 1] & 0x0F) << 8) | data[off + 2];
                var end = off + 3 + secLen - 4;
                var progInfoLen = ((data[off + 10] & 0x0F) << 8) | data[off + 11];
                off += 12 + progInfoLen;

                while (off + 5 <= end)
                {
                    var streamType = data[off];
                    var esPid = ((data[off + 1] & 0x1F) << 8) | data[off + 2];
                    var esInfoLen = ((data[off + 3] & 0x0F) << 8) | data[off + 4];

                    if (streamType == H264StreamType)
                    {
                        return esPid;
                    }

                    off += 5 + esInfoLen;
                }

                break;
            }

            return -1;
        }

        // ── PES reassembly ────────────────────────────────────────────────────

        private static void ProcessVideoPackets(byte[] data, int videoPid)
        {
            var chunks = new List<(int Offset, int Length)>();
            var havePes = false;

            void FlushPes()
            {
                if (havePes && chunks.Count > 0)
                {
                    StripTrailingSeisFromPes(data, chunks);
                }

                chunks.Clear();
            }

            for (var i = 0; i + TsPacketSize <= data.Length; i += TsPacketSize)
            {
                if (data[i] != TsSyncByte) continue;
                if (GetPid(data, i) != videoPid) continue;

                var pusi = (data[i + 1] & 0x40) != 0;
                var ps = GetPayloadStart(data, i);
                if (ps < 0) continue;

                var len = i + TsPacketSize - ps;
                if (len <= 0) continue;

                if (pusi)
                {
                    FlushPes();
                    havePes = true;
                }

                if (havePes)
                {
                    chunks.Add((ps, len));
                }
            }

            FlushPes();
        }

        // ── PES → NAL scanning ────────────────────────────────────────────────

        private static void StripTrailingSeisFromPes(
            byte[] data, List<(int Offset, int Length)> chunks)
        {
            // Build flat payload + position map (pesPayload[i] ↔ data[offsets[i]])
            var total = 0;
            foreach (var (_, len) in chunks) total += len;

            var payload = new byte[total];
            var offsets = new int[total];
            var idx = 0;
            foreach (var (off, len) in chunks)
            {
                for (var j = 0; j < len; j++)
                {
                    payload[idx] = data[off + j];
                    offsets[idx] = off + j;
                    idx++;
                }
            }

            var h264Start = FindH264Start(payload);
            if (h264Start < 0) return;

            StripNals(data, payload, offsets, h264Start);
        }

        // Returns the offset of the first H.264 Annex-B byte inside a PES payload.
        private static int FindH264Start(byte[] pes)
        {
            // Minimum usable PES for video: 9 bytes (3 start + stream_id + 2 length + 3 flags/headerLen)
            if (pes.Length < 9) return -1;
            if (pes[0] != 0x00 || pes[1] != 0x00 || pes[2] != 0x01) return -1;

            var streamId = pes[3];
            if (streamId < 0xE0 || streamId > 0xEF) return -1; // video stream IDs

            var headerDataLen = pes[8];
            var start = 9 + headerDataLen;
            return start < pes.Length ? start : -1;
        }

        // ── NAL processing ────────────────────────────────────────────────────

        private static void StripNals(byte[] data, byte[] payload, int[] offsets, int h264Start)
        {
            if (!TryFindStartCode(payload, h264Start, out var scPos, out var scLen))
            {
                return;
            }

            var seenVcl = false;

            while (true)
            {
                var headerPos = scPos + scLen;
                if (headerPos >= payload.Length) break;

                var nalType = payload[headerPos] & 0x1F;

                TryFindStartCode(payload, headerPos + 1, out var nextScPos, out var nextScLen);
                var nalEnd = nextScPos >= 0 ? nextScPos : payload.Length;

                if (nalType is 1 or 5)
                {
                    seenVcl = true;
                }
                else if (nalType == 6 && seenVcl)
                {
                    ReplaceWithFillerData(data, offsets, headerPos, nalEnd);
                }

                if (nextScPos < 0) break;
                scPos = nextScPos;
                scLen = nextScLen;
            }
        }

        /// <summary>
        /// Replaces a SEI NAL unit (starting at <paramref name="headerPos"/>) with a
        /// filler_data NAL unit of the same byte length.
        /// </summary>
        private static void ReplaceWithFillerData(
            byte[] data, int[] offsets, int headerPos, int nalEnd)
        {
            // NAL unit type: SEI (6) → filler_data (12)
            data[offsets[headerPos]] = 0x0C;

            // Content: FF...FF 0x80 (filler bytes + RBSP trailing bits)
            var contentStart = headerPos + 1;
            var contentLen = nalEnd - contentStart;
            for (var i = 0; i < contentLen - 1; i++)
            {
                data[offsets[contentStart + i]] = 0xFF;
            }

            if (contentLen > 0)
            {
                data[offsets[contentStart + contentLen - 1]] = 0x80;
            }
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        private static bool TryFindStartCode(byte[] buf, int from, out int pos, out int len)
        {
            for (var i = from; i < buf.Length - 2; i++)
            {
                if (buf[i] != 0x00 || buf[i + 1] != 0x00) continue;
                if (buf[i + 2] == 0x01) { pos = i; len = 3; return true; }
                if (i + 3 < buf.Length && buf[i + 2] == 0x00 && buf[i + 3] == 0x01)
                {
                    pos = i; len = 4; return true;
                }
            }

            pos = -1;
            len = 0;
            return false;
        }

        private static int GetPid(byte[] data, int offset)
            => ((data[offset + 1] & 0x1F) << 8) | data[offset + 2];

        private static int GetPayloadStart(byte[] data, int offset)
        {
            var afc = (data[offset + 3] >> 4) & 0x3;
            var hasAdaptation = (afc & 2) != 0;
            var hasPayload = (afc & 1) != 0;
            if (!hasPayload) return -1;

            var start = offset + 4;
            if (hasAdaptation)
            {
                if (start >= data.Length) return -1;
                start += 1 + data[start];
            }

            return start < offset + TsPacketSize ? start : -1;
        }
    }
}
