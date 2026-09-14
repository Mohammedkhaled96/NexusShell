using Microsoft.Extensions.Logging;
using NexusShell.App.Interfaces;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using static NexusShell.App.Services.NativeMethods;
namespace NexusShell.App.Services
{
    public class TerminalSession : ITerminalSession
    {
        private const int ReadBufferSize    = 4096;
        private const int WriteInputDelayMs = 50;
        private const int DefaultColumns    = 120;
        private const int DefaultRows       = 30;

        private readonly ILogger<TerminalSession> _logger;
        private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
        private IntPtr _hPC = IntPtr.Zero;
        private IntPtr _hPipeInputRead = IntPtr.Zero, _hPipeInputWrite = IntPtr.Zero;
        private IntPtr _hPipeOutputRead = IntPtr.Zero, _hPipeOutputWrite = IntPtr.Zero;
        private PROCESS_INFORMATION _processInfo;
        private FileStream? _outputStream;
        private Thread? _outputThread;
        private CancellationTokenSource? _cancellationTokenSource;
        private bool _disposed;
        private int _lastColumns = DefaultColumns;
        private int _lastRows    = DefaultRows;

        public event Action<string>? OutputReceived;
        public event Action<Exception>? UnrecoverableError;
        public TerminalSession(ILogger<TerminalSession> logger)
        {
            _logger = logger;
        }
        public void Start(string commandLine, string? workingDirectory = null)
        {
            _logger.LogInformation("Starting terminal session with command: {CommandLine} in {WorkingDirectory}", commandLine, workingDirectory ?? "Default");
            _cancellationTokenSource = new CancellationTokenSource();
            try
            {
                if (!CreatePipe(out _hPipeInputRead, out _hPipeInputWrite, IntPtr.Zero, 0))
                    throw new IOException("Failed to create input pipes.");
                if (!CreatePipe(out _hPipeOutputRead, out _hPipeOutputWrite, IntPtr.Zero, 0))
                    throw new IOException("Failed to create output pipes.");
                var consoleSize = new COORD { X = (short)_lastColumns, Y = (short)_lastRows };
                int hr = CreatePseudoConsole(consoleSize, _hPipeInputRead, _hPipeOutputWrite, 0, out _hPC);
                if (hr != S_OK)
                {
                    throw new IOException($"Failed to create Pseudo Console. Error Code: {hr}");
                }

                // Close the handles that have been duplicated into the ConPTY process.
                // We (the parent) only need to write to Input and read from Output.
                if (_hPipeInputRead != IntPtr.Zero) { CloseHandle(_hPipeInputRead); _hPipeInputRead = IntPtr.Zero; }
                if (_hPipeOutputWrite != IntPtr.Zero) { CloseHandle(_hPipeOutputWrite); _hPipeOutputWrite = IntPtr.Zero; }

                _outputStream = new FileStream(new SafeFileHandle(_hPipeOutputRead, false), FileAccess.Read);
                RunProcessAttachedToPseudoConsole(commandLine, workingDirectory);
                _outputThread = new Thread(ReadOutputLoop) { IsBackground = true, Name = "TerminalOutputReader" };
                _outputThread.Start();
                _logger.LogInformation("Terminal session started successfully. Process ID: {ProcessId}", _processInfo.dwProcessId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start terminal session.");
                Dispose();
                throw;
            }
        }        public void Resize(int columns, int rows)
        {
            _lastColumns = columns;
            _lastRows = rows;

            if (_hPC != IntPtr.Zero)
            {
                _logger.LogDebug("Resizing terminal to {Columns}x{Rows}", columns, rows);
                ResizePseudoConsole(_hPC, new COORD { X = (short)columns, Y = (short)rows });
            }
        }

        public int ProcessId => _processInfo.dwProcessId;

        public bool HasChildProcesses()
        {
            int parentPid = _processInfo.dwProcessId;
            if (parentPid <= 0) return false;

            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return false;

            try
            {
                var pe = new PROCESSENTRY32();
                pe.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();

                if (Process32First(snapshot, ref pe))
                {
                    do
                    {
                        if (pe.th32ParentProcessID == (uint)parentPid)
                        {
                            return true;
                        }
                    } while (Process32Next(snapshot, ref pe));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error checking child processes");
            }
            finally
            {
                CloseHandle(snapshot);
            }

            return false;
        }

        public void Refresh()
        {
            // Force a flush by re-issuing the resize command with the current dimensions.
            // This triggers the ConPTY renderer to redraw/flush pending frames.
            if (_hPC != IntPtr.Zero)
            {
                _logger.LogDebug("Refreshing terminal session (Flushing buffers via Resize).");
                ResizePseudoConsole(_hPC, new COORD { X = (short)_lastColumns, Y = (short)_lastRows });
            }
        }

        public void WriteInput(string input)
        {
            if (_hPipeInputWrite == IntPtr.Zero || _cancellationTokenSource == null || _cancellationTokenSource.IsCancellationRequested) return;

            try
            {
                // Do not log the raw input — it may contain secrets the user typed
                // (passwords, tokens, connection strings). Log only the size.
                _logger.LogDebug("Writing {ByteCount} bytes of input to terminal.", Encoding.UTF8.GetByteCount(input));

                // 1. Send the command text
                byte[] commandBytes = Encoding.UTF8.GetBytes(input);
                if (commandBytes.Length > 0)
                {
                    if (!WriteFile(_hPipeInputWrite, commandBytes, commandBytes.Length, out _, IntPtr.Zero))
                    {
                        throw new IOException($"WriteFile (Command) failed. Error: {Marshal.GetLastWin32Error()}");
                    }
                }

                // A small delay is the only known-working solution to prevent a race condition
                // with the ConPTY layer and the child process. 50ms is a compromise.
                Thread.Sleep(WriteInputDelayMs);

                // 2. Send the newline (Enter)
                byte[] newlineBytes = Encoding.UTF8.GetBytes("\r\n");
                if (!WriteFile(_hPipeInputWrite, newlineBytes, newlineBytes.Length, out _, IntPtr.Zero))
                {
                    throw new IOException($"WriteFile (Newline) failed. Error: {Marshal.GetLastWin32Error()}");
                }

                // 3. Flush at the end
                FlushFileBuffers(_hPipeInputWrite);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write to terminal stream.");
                UnrecoverableError?.Invoke(new IOException("Cannot write to a closed terminal session."));
            }
        }

        /// <inheritdoc />
        public void WriteRawInput(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            if (_hPipeInputWrite == IntPtr.Zero || _cancellationTokenSource == null || _cancellationTokenSource.IsCancellationRequested) return;

            try
            {
                if (!WriteFile(_hPipeInputWrite, data, data.Length, out _, IntPtr.Zero))
                {
                    throw new IOException($"WriteFile (RawInput) failed. Error: {Marshal.GetLastWin32Error()}");
                }
                FlushFileBuffers(_hPipeInputWrite);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write raw input to terminal stream.");
            }
        }

        private void RunProcessAttachedToPseudoConsole(string commandLine, string? workingDirectory)
        {
            var startupInfo = new STARTUPINFOEX();
            startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            IntPtr lpSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
            startupInfo.lpAttributeList = Marshal.AllocHGlobal(lpSize);
            try
            {
                InitializeProcThreadAttributeList(startupInfo.lpAttributeList, 1, 0, ref lpSize);
                UpdateProcThreadAttribute(startupInfo.lpAttributeList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero);
                bool success = CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0x00080000, IntPtr.Zero, workingDirectory, ref startupInfo, out _processInfo);
                if (!success)
                {
                    throw new IOException($"Failed to create process '{commandLine}'. Error Code: {Marshal.GetLastWin32Error()}");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(startupInfo.lpAttributeList);
            }
        }
        private void ReadOutputLoop()
        {
            if (_outputStream == null || _cancellationTokenSource == null) return;
            _logger.LogInformation("Output reader thread started.");
            var buffer = new byte[ReadBufferSize];
            var token = _cancellationTokenSource.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    int bytesRead = _outputStream.Read(buffer, 0, buffer.Length);
                    if (bytesRead <= 0)
                    {
                        _logger.LogInformation("Output stream ended (bytesRead <= 0). Terminating reader thread.");
                        break;
                    }
                    int charCount = _utf8Decoder.GetCharCount(buffer, 0, bytesRead);
                    char[] chars = new char[charCount];
                    _utf8Decoder.GetChars(buffer, 0, bytesRead, chars, 0);
                    string content = new string(chars);
                    OutputReceived?.Invoke(content);
                }
                catch (Exception ex) when (ex is ObjectDisposedException || ex is IOException)
                {
                    _logger.LogInformation("Output stream was closed. Terminating reader thread gracefully.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An unhandled exception occurred in the output reader thread."); 
                    UnrecoverableError?.Invoke(ex);
                    break;
                }
            }
            _logger.LogInformation("Output reader thread finished.");
        }
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _logger.LogInformation("Disposing terminal session. Managed resources: {IsManaged}", disposing);
            if (disposing)
            {
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource?.Dispose();
                _outputStream?.Dispose();
            }
            if (_processInfo.hProcess != IntPtr.Zero)
            {
                _logger.LogInformation("Terminating child process {ProcessId}", _processInfo.dwProcessId);
                try { TerminateProcess(_processInfo.hProcess, 1); } catch {  }
                CloseHandle(_processInfo.hProcess);
                _processInfo.hProcess = IntPtr.Zero;
            }
            if (_processInfo.hThread != IntPtr.Zero) { CloseHandle(_processInfo.hThread); _processInfo.hThread = IntPtr.Zero; }
            if (_hPC != IntPtr.Zero) { _logger.LogDebug("Closing pseudoconsole handle."); ClosePseudoConsole(_hPC); _hPC = IntPtr.Zero; }
            if (_hPipeInputRead != IntPtr.Zero) { CloseHandle(_hPipeInputRead); _hPipeInputRead = IntPtr.Zero; }
            if (_hPipeInputWrite != IntPtr.Zero) { CloseHandle(_hPipeInputWrite); _hPipeInputWrite = IntPtr.Zero; }
            if (_hPipeOutputRead != IntPtr.Zero) { CloseHandle(_hPipeOutputRead); _hPipeOutputRead = IntPtr.Zero; }
            if (_hPipeOutputWrite != IntPtr.Zero) { CloseHandle(_hPipeOutputWrite); _hPipeOutputWrite = IntPtr.Zero; }
            _disposed = true;
            _logger.LogInformation("Terminal session disposed.");
        }
        ~TerminalSession()
        {
            Dispose(false);
        }
    }
}