using System;
using System.IO;
using System.Text;

namespace RSTGameTranslation
{
    /// <summary>
    /// Mirrors everything written to Console (Console.WriteLine) into a per-session
    /// log file under app\logs so diagnostics survive after the app closes and can be
    /// inspected even when the process runs elevated (detached stdout).
    /// LogWindow later wraps Console.Out again; because it captures our tee as its
    /// "original" writer, file logging keeps working whether or not the log window is open.
    /// </summary>
    public static class DebugFileLogger
    {
        private static bool _initialized;
        private static TextWriter? _fileWriter;

        public static string? CurrentLogPath { get; private set; }

        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(dir);

                string path = Path.Combine(dir, $"session_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                _fileWriter = new StreamWriter(path, append: true) { AutoFlush = true };

                CurrentLogPath = path;
                Console.SetOut(new TeeTextWriter(Console.Out, _fileWriter));
                Console.WriteLine($"[DebugFileLogger] Session log started: {path}");
            }
            catch
            {
                // Logging is best-effort; never block startup on it.
            }
        }

        /// <summary>
        /// Wraps a console writer so it also tees to the session log file. Call this whenever
        /// code replaces Console.Out (e.g. AllocConsole setup) so file logging survives.
        /// </summary>
        public static TextWriter WrapWithFile(TextWriter inner)
        {
            return _fileWriter != null ? new TeeTextWriter(inner, _fileWriter) : inner;
        }
    }

    /// <summary>
    /// TextWriter that forwards every write to two sinks (console + file), guarded by a
    /// lock so interleaved writes from OCR/translation threads stay line-consistent.
    /// </summary>
    public class TeeTextWriter : TextWriter
    {
        private readonly TextWriter _primary;
        private readonly TextWriter _secondary;
        private readonly object _lock = new object();

        public TeeTextWriter(TextWriter primary, TextWriter secondary)
        {
            _primary = primary;
            _secondary = secondary;
        }

        public override Encoding Encoding => _secondary.Encoding;

        public override void Write(char value)
        {
            lock (_lock)
            {
                _primary.Write(value);
                _secondary.Write(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_lock)
            {
                _primary.Write(value);
                _secondary.Write(value);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_lock)
            {
                _primary.WriteLine(value);
                _secondary.WriteLine(value);
            }
        }

        public override void Flush()
        {
            lock (_lock)
            {
                _primary.Flush();
                _secondary.Flush();
            }
        }
    }
}
