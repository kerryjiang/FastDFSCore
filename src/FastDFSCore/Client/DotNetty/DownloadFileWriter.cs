﻿using DotNetty.Common.Internal.Logging;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace FastDFSCore.Client
{
    /// <summary>下载文件写入
    /// </summary>
    public class DownloadFileWriter
    {

        private int _isComplete = 0;
        private readonly FileStream _fs;
        private readonly ILogger _logger;
        private readonly ConcurrentQueue<byte[]> _pendingWriteQueue = new ConcurrentQueue<byte[]>();
        public DownloadFileWriter(FDFSOption option, string filePath)
        {
            _logger = InternalLoggerFactory.DefaultFactory.CreateLogger(option.LoggerName);
            _fs = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        }

        public void Run()
        {
            ThreadPool.QueueUserWorkItem(s =>
            {
                while (_isComplete == 0)
                {
                    try
                    {
                        byte[] buffers;
                        if (_pendingWriteQueue.TryDequeue(out buffers))
                        {
                            _fs.Write(buffers, 0, buffers.Length);
                        }
                        else
                        {
                            Thread.Sleep(3);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("写入文件发生错误,{0}", ex.Message);
                        Interlocked.Exchange(ref _isComplete, 1);
                    }
                }
            }, null);
        }


        /// <summary>写入数据
        /// </summary>
        public void WriteBuffers(byte[] buffers)
        {
            _pendingWriteQueue.Enqueue(buffers);
        }



        public void WriteComplete()
        {
            while (!_pendingWriteQueue.IsEmpty)
            {
                Thread.Sleep(1);
            }
            Interlocked.Exchange(ref _isComplete, 1);
            if (_fs != null)
            {
                _fs.Flush();
            }
        }

        public void Release()
        {
            Interlocked.Exchange(ref _isComplete, 1);
            if (_fs != null)
            {
                _fs.Close();
                _fs.Dispose();
            }
        }

    }
}