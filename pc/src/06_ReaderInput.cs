using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text;

namespace Decimen
{
    // The handheld 2D code reader, on a serial port.
    //
    // Two ways a reader hands over what it scanned, and they need different
    // things from us:
    //
    //   KEYBOARD WEDGE — the reader types the text. That needs no code here at
    //     all: the page puts a focused input on screen in reader mode and the
    //     keystrokes land in it. Deliberately NOT a global keyboard hook. A
    //     WH_KEYBOARD_LL hook would capture every keystroke on the machine,
    //     which is a keylogger whatever it is for, and is incompatible with the
    //     promise that this product's log never contains what you typed.
    //
    //   SERIAL (COM) — the reader speaks over a port. That does need the host,
    //     because a page cannot open one. This class is that, and only that.
    //
    // Note this is a 2D code reader. An ordinary one-dimensional barcode
    // scanner cannot read these codes at all, and nothing here pretends
    // otherwise: the frames are QR symbols carrying several hundred bytes.
    public sealed class ReaderInput : IDisposable
    {
        private SerialPort port;
        private readonly StringBuilder pending = new StringBuilder();
        private readonly Action<string> onLine;
        private readonly Action<string, string> onStatus;

        public ReaderInput(Action<string> onLine, Action<string, string> onStatus)
        {
            this.onLine = onLine;
            this.onStatus = onStatus;
        }

        public bool IsOpen { get { return port != null && port.IsOpen; } }
        public string PortName { get { return port == null ? null : port.PortName; } }

        public static List<string> AvailablePorts()
        {
            List<string> names = new List<string>();
            try
            {
                names.AddRange(SerialPort.GetPortNames());
            }
            catch
            {
                // A machine with no serial subsystem answers with nothing,
                // which is the honest answer.
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        public void Open(string portName, int baud)
        {
            Close();
            if (string.IsNullOrEmpty(portName))
            {
                throw new HostActionException("no-port", "COM ポートを選んでください。");
            }
            try
            {
                port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One);
                port.ReadTimeout = 500;
                port.NewLine = "\r";
                port.Encoding = Encoding.ASCII;
                port.DataReceived += OnDataReceived;
                port.ErrorReceived += OnErrorReceived;
                port.Open();
                onStatus("open", portName + " を開きました。読み取り機のトリガーを引いてください。");
            }
            catch (UnauthorizedAccessException)
            {
                port = null;
                throw new HostActionException(
                    "port-busy",
                    portName + " は別のプログラムが使っています。そちらを閉じてからやり直してください。");
            }
            catch (Exception error)
            {
                port = null;
                throw new HostActionException("port-failed", portName + " を開けません: " + error.Message);
            }
        }

        public void Close()
        {
            if (port == null)
            {
                return;
            }
            try
            {
                port.DataReceived -= OnDataReceived;
                port.ErrorReceived -= OnErrorReceived;
                if (port.IsOpen)
                {
                    port.Close();
                }
            }
            catch
            {
            }
            port = null;
            pending.Length = 0;
        }

        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                string chunk = port.ReadExisting();
                Feed(chunk);
            }
            catch (Exception error)
            {
                onStatus("error", "読み取り機からの受信に失敗しました: " + error.Message);
            }
        }

        private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            onStatus("error", "読み取り機との通信でエラーが起きました（" + e.EventType.ToString() + "）。");
        }

        /// Split on CR and LF, in whatever combination the reader is configured
        /// to send — a suffix of CR, of LF, of both, or of neither with the
        /// next scan simply following on. Exposed so the emulator test can feed
        /// the same parser the port does.
        public void Feed(string chunk)
        {
            foreach (char c in chunk)
            {
                if (c == '\r' || c == '\n')
                {
                    Flush();
                    continue;
                }
                pending.Append(c);
                // A reader stuck in "continuous" mode with nothing to read can
                // stream forever; a frame is never anywhere near this long.
                if (pending.Length > 8192)
                {
                    pending.Length = 0;
                    onStatus("error", "読み取り機から想定外に長いデータが届きました。設定を確かめてください。");
                }
            }
        }

        private void Flush()
        {
            string line = pending.ToString().Trim();
            pending.Length = 0;
            if (line.Length == 0)
            {
                return;
            }
            onLine(line);
        }

        public void Dispose()
        {
            Close();
        }
    }
}
