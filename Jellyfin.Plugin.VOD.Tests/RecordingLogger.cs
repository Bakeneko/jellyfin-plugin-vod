using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Tests
{
    /// <summary>
    /// A logger that keeps what it was told, so a test can assert on a level and a message.
    /// </summary>
    /// <typeparam name="T">The category the logger is for.</typeparam>
    /// <remarks>
    /// Several pieces of the publication pass express a decision only as a log line — the debug
    /// line on an empty pass, the warning on the turn cap, the warning on a lane the pass gave up
    /// on. <c>NullLogger</c> cannot show any of them, and they are the only observable those
    /// branches have.
    /// </remarks>
    internal sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => Scope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class Scope : IDisposable
        {
            internal static readonly Scope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
