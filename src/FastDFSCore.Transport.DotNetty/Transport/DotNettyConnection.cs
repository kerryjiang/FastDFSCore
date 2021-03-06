﻿using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using DotNetty.Handlers.Logging;
using DotNetty.Handlers.Streams;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using FastDFSCore.Protocols;
using FastDFSCore.Transport.DotNetty;
using FastDFSCore.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace FastDFSCore.Transport
{
    /// <summary>DotNetty连接
    /// </summary>
    public class DotNettyConnection : BaseConnection
    {
        private IEventLoopGroup _group;
        private IChannel _channel;
        private Bootstrap _bootStrap;

        private int _reConnectAttempt = 0;
        private readonly SemaphoreSlim _semaphoreSlim;

        private TransportContext _context = null;

        private TaskCompletionSource<FastDFSResp> _taskCompletionSource = null;

        public DotNettyConnection(ILogger<BaseConnection> logger, IServiceProvider serviceProvider, IOptions<FastDFSOption> option, ConnectionAddress connectionAddress) : base(logger, serviceProvider, option, connectionAddress)
        {
            _semaphoreSlim = new SemaphoreSlim(1);
        }


        /// <summary>发送数据
        /// </summary>
        public override Task<FastDFSResp> SendRequestAsync<T>(FastDFSReq<T> request)
        {
            _taskCompletionSource = new TaskCompletionSource<FastDFSResp>();
            //上下文,当前的信息
            _context = BuildContext<T>(request);

            var bodyBuffer = request.EncodeBody(Option);
            if (request.Header.Length == 0)
            {
                request.Header.Length = request.InputStream != null ? request.InputStream.Length + bodyBuffer.Length : bodyBuffer.Length;
            }

            var headerBuffer = request.Header.ToBytes();
            var newBuffer = ByteUtil.Combine(headerBuffer, bodyBuffer);

            //流文件发送
            if (request.InputStream != null)
            {
                _channel.WriteAsync(Unpooled.WrappedBuffer(newBuffer));
                var stream = new FixChunkedStream(request.InputStream);
                _channel.WriteAndFlushAsync(stream);
            }
            else
            {
                _channel.WriteAndFlushAsync(Unpooled.WrappedBuffer(newBuffer));
            }
            return _taskCompletionSource.Task;
        }


        /// <summary>运行
        /// </summary>
        public override async Task ConnectAsync()
        {
            if (_channel != null && _channel.Registered)
            {
                Logger.LogInformation($"Client is running! Don't run again! ChannelId:{_channel.Id.AsLongText()}");
                return;
            }

            try
            {
                _group = new MultithreadEventLoopGroup();
                _bootStrap = new Bootstrap();
                _bootStrap
                    .Group(_group)
                    .Channel<TcpSocketChannel>()
                    .Option(ChannelOption.TcpNodelay, true)
                    .Option(ChannelOption.WriteBufferHighWaterMark, 16777216)
                    .Option(ChannelOption.WriteBufferLowWaterMark, 8388608)
                    //.Option(ChannelOption.SoReuseaddr, tcpSetting.SoReuseaddr)
                    .Option(ChannelOption.AutoRead, true)
                    .Handler(new ActionChannelInitializer<ISocketChannel>(channel =>
                    {
                        IChannelPipeline pipeline = channel.Pipeline;
                        pipeline.AddLast(new LoggingHandler(typeof(DotNettyConnection)));
                        pipeline.AddLast("fdfs-write", new ChunkedWriteHandler<IByteBuffer>());
                        pipeline.AddLast("fdfs-decoder", ServiceProvider.CreateInstance<FastDFSDecoder>(new Func<TransportContext>(GetContext)));
                        pipeline.AddLast("fdfs-read", ServiceProvider.CreateInstance<FastDFSHandler>(new Action<ReceivedPackage>(SetResponse)));

                        //重连
                        if (Option.EnableReConnect)
                        {
                            //Reconnect to server
                            pipeline.AddLast("reconnect", ServiceProvider.CreateInstance<ReConnectHandler>(Option, new Func<Task>(DoReConnectIfNeed)));
                        }

                    }));

                await DoConnect();

                IsRunning = true;
                Logger.LogInformation($"Client Run! serverEndPoint:{_channel.RemoteAddress.ToStringAddress()},localAddress:{_channel.LocalAddress.ToStringAddress()}");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex.Message);
                await _group.ShutdownGracefullyAsync(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
            }
        }


        /// <summary>关闭连接
        /// </summary>
        public override async Task CloseAsync()
        {
            try
            {
                await _channel.CloseAsync();
                IsRunning = false;
            }
            finally
            {
                await _group.ShutdownGracefullyAsync(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
            }
        }


        /// <summary>连接操作
        /// </summary>
        private async Task DoConnect()
        {
            _channel = await _bootStrap.ConnectAsync(new IPEndPoint(IPAddress.Parse(ConnectionAddress.IPAddress), ConnectionAddress.Port));
            Logger.LogInformation($"Client DoConnect! Id:{Id},serverEndPoint:{_channel.RemoteAddress.ToStringAddress()},localAddress:{_channel.LocalAddress.ToStringAddress()}");

        }

        /// <summary>重连机制
        /// </summary>
        private async Task DoReConnectIfNeed()
        {
            if (!Option.EnableReConnect || Option.ReConnectMaxCount < _reConnectAttempt || !IsRunning)
            {
                return;
            }
            if (true)
            {
                await _semaphoreSlim.WaitAsync();
                bool reConnectSuccess = false;
                try
                {
                    Logger.LogInformation($"Try to reconnect server!");
                    //await DoConnect();
                    Interlocked.Exchange(ref _reConnectAttempt, 0);
                    reConnectSuccess = true;
                }
                catch (Exception ex)
                {
                    Logger.LogError("ReConnect fail!{0}", ex.Message);
                }
                finally
                {
                    Interlocked.Increment(ref _reConnectAttempt);
                    _semaphoreSlim.Release();
                }
                //Try again!
                if (_reConnectAttempt < Option.ReConnectMaxCount && !reConnectSuccess)
                {
                    Thread.Sleep(Option.ReConnectIntervalMilliSeconds);
                    await DoReConnectIfNeed();
                }
            }
        }


        private TransportContext GetContext()
        {
            return _context;
        }


        /// <summary>设置返回值
        /// </summary>
        private void SetResponse(ReceivedPackage package)
        {
            try
            {
                //返回为Strem,需要逐步进行解析
                var response = _context.Response;
                response.Header = new FastDFSHeader(package.Length, package.Command, package.Status);

                if (!_context.IsOutputStream)
                {
                    response.LoadContent(Option, package.Body);
                }

                _taskCompletionSource.SetResult(response);

                //释放
                ReferenceCountUtil.SafeRelease(package);

            }
            catch (Exception ex)
            {
                Logger.LogError("接收返回信息出错! {0}", ex);
                throw;
            }
        }



    }
}
