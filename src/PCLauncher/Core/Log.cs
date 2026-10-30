using System;
using System.IO;
using System.Text;

namespace PCLauncher.Core
{
    /// <summary>
    /// 日志：每次写入都以追加方式打开文件再关闭。
    /// 这样父域 / 子域各自写自己的行，不会因为持有各自的文件指针而互相覆盖。
    /// </summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;

        public static string Path_ { get { return _path; } }

        public static void Start()
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "runtime", "logs");
                Directory.CreateDirectory(dir);
                _path = System.IO.Path.Combine(dir, "PCL-MCDownload-fixer-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                File.WriteAllText(_path, "", new UTF8Encoding(false));
                Write("INFO", "日志开始：" + _path);
            }
            catch (Exception)
            {
                _path = null;
            }
        }

        /// <summary>子 AppDomain 复用父域的日志文件。</summary>
        public static void Attach(string path)
        {
            _path = path;
        }

        public static void Stop() { }

        public static void Info(string msg) { Write("INFO", msg); }
        public static void Warn(string msg) { Write("WARN", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }

        public static void Error(string msg, Exception ex)
        {
            Write("ERROR", msg + Environment.NewLine + ex);
        }

        public static void Write(string level, string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + msg;
            lock (Gate)
            {
                try
                {
                    if (_path != null)
                    {
                        using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                        {
                            w.WriteLine(line);
                            w.Flush();
                        }
                    }
                }
                catch (Exception) { }
                try { Console.WriteLine(line); } catch (Exception) { }
            }
        }
    }
}
