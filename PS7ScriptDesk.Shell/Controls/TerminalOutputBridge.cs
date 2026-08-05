using System.Text;
using System.Text.Json;

namespace PS7ScriptDesk.Shell.Controls
{
    internal sealed class TerminalOutputBatchBuffer
    {
        private readonly object _syncRoot = new();
        private readonly StringBuilder _buffer = new();
        private bool _flushScheduled;

        public bool Enqueue(string data)
        {
            if (string.IsNullOrEmpty(data))
            {
                return false;
            }

            lock (_syncRoot)
            {
                _buffer.Append(data);
                if (_flushScheduled)
                {
                    return false;
                }

                _flushScheduled = true;
                return true;
            }
        }

        public string Drain()
        {
            lock (_syncRoot)
            {
                if (_buffer.Length == 0)
                {
                    _flushScheduled = false;
                    return string.Empty;
                }

                var data = _buffer.ToString();
                _buffer.Clear();
                _flushScheduled = false;
                return data;
            }
        }
    }

    internal static class TerminalWebMessageSerializer
    {
        public static string Serialize(string type, string data)
        {
            return type switch
            {
                "output" => JsonSerializer.Serialize(new
                {
                    type = "output_b64",
                    data = Convert.ToBase64String(Encoding.UTF8.GetBytes(data ?? string.Empty))
                }),
                "clear" or "focus" => JsonSerializer.Serialize(new { type }),
                _ => JsonSerializer.Serialize(new { type, data })
            };
        }
    }
}
