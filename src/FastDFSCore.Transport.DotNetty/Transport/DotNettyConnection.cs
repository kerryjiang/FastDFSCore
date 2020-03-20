﻿using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using DotNetty.Handlers.Logging;
using DotNetty.Handlers.Streams;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using FastDFSCore.Codecs.Messages;
using FastDFSCore.Extensions;
using FastDFSCore.Transport.DotNetty;
using FastDFSCore.Transport.Download;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
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

        private ConnectionContext _connectionContext;
        private TaskCompletionSource<FDFSResponse> _taskCompletionSource = null;
        private IDownloader _downloader = null;

        /// <summary>Ctor
        /// </summary>
        public DotNettyConnection(IServiceProvider provider, ILogger<BaseConnection> logger, FDFSOption option, ConnectionAddress connectionAddress) : base(provider, logger, option, connectionAddress)
        {

        }

        /// <summary>运行
        /// </summary>
        public override async Task RunAsync()
        {
            if (_channel != null && _channel.Registered)
            {
                Logger.LogInformation($"Client is running! Don't run again! ChannelId:{_channel.Id.AsLongText()}");
                return;
            }
            var tcpSetting = Option.TcpSetting;
            try
            {

                _group = new MultithreadEventLoopGroup();
                _bootStrap = new Bootstrap();
                _bootStrap
                    .Group(_group)
                    .Channel<TcpSocketChannel>()
                    .Option(ChannelOption.TcpNodelay, tcpSetting.TcpNodelay)
                    .Option(ChannelOption.WriteBufferHighWaterMark, tcpSetting.WriteBufferHighWaterMark)
                    .Option(ChannelOption.WriteBufferLowWaterMark, tcpSetting.WriteBufferLowWaterMark)
                    .Option(ChannelOption.SoRcvbuf, tcpSetting.SoRcvbuf)
                    .Option(ChannelOption.SoSndbuf, tcpSetting.SoSndbuf)
                    .Option(ChannelOption.SoReuseaddr, tcpSetting.SoReuseaddr)
                    .Option(ChannelOption.AutoRead, true)
                    .Handler(new ActionChannelInitializer<ISocketChannel>(channel =>
                    {
                        IChannelPipeline pipeline = channel.Pipeline;
                        pipeline.AddLast(new LoggingHandler(typeof(DotNettyConnection)));
                        pipeline.AddLast("fdfs-write", new ChunkedWriteHandler<IByteBuffer>());
                        pipeline.AddLast("fdfs-decoder", new FDFSDecoder(GetContext));
                        pipeline.AddLast("fdfs-read", new FDFSReadHandler(SetResponse));

                        //重连
                        if (Option.TcpSetting.EnableReConnect)
                        {
                            //Reconnect to server
                            //pipeline.AddLast("reconnect", Provider.CreateInstance<ReConnectHandler>(Option, new Func<Task>(DoReConnectIfNeed)));
                        }

                    }));

                await DoConnect();

                _isRunning = true;
                Logger.LogInformation($"Client Run! serverEndPoint:{_channel.RemoteAddress.ToStringAddress()},localAddress:{_channel.LocalAddress.ToStringAddress()}");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex.Message);
                await _group.ShutdownGracefullyAsync(TimeSpan.FromMilliseconds(tcpSetting.QuietPeriodMilliSeconds), TimeSpan.FromSeconds(tcpSetting.CloseTimeoutSeconds));
            }
        }


        /// <summary>关闭连接
        /// </summary>
        public override async Task ShutdownAsync()
        {
            try
            {
                await _channel.CloseAsync();
                _isRunning = false;
            }
            finally
            {
                await _group.ShutdownGracefullyAsync(TimeSpan.FromMilliseconds(Option.TcpSetting.QuietPeriodMilliSeconds), TimeSpan.FromSeconds(Option.TcpSetting.CloseTimeoutSeconds));
            }
        }

        /// <summary>释放连接
        /// </summary>
        public override async Task DisposeAsync()
        {
            await ShutdownAsync();
            _connectionContext = null;
            _downloader?.Release();
        }

        /// <summary>发送数据
        /// </summary>
        public override Task<FDFSResponse> SendRequestAsync<T>(FDFSRequest<T> request)
        {
            _taskCompletionSource = new TaskCompletionSource<FDFSResponse>();
            //上下文,当前的信息
            _connectionContext = CreateContext<T>(request);
            //初始化保存流
            if (_connectionContext.StreamResponse && request.Downloader != null)
            {
                _downloader = request.Downloader;
                _downloader?.Run();
            }

            var bodyBuffer = request.EncodeBody(Option);
            if (request.Header.Length == 0)
            {
                request.Header.Length = request.StreamRequest ? request.RequestStream.Length + bodyBuffer.Length : bodyBuffer.Length;
            }

            var headerBuffer = request.Header.ToBytes();
            List<byte> newBuffer = new List<byte>();
            newBuffer.AddRange(headerBuffer);
            newBuffer.AddRange(bodyBuffer);

            //流文件发送
            if (request.StreamRequest)
            {
                _channel.WriteAsync(Unpooled.WrappedBuffer(newBuffer.ToArray()));
                var stream = new FixChunkedStream(request.RequestStream);
                _channel.WriteAndFlushAsync(stream);
            }
            else
            {
                _channel.WriteAndFlushAsync(Unpooled.WrappedBuffer(newBuffer.ToArray()));
            }
            return _taskCompletionSource.Task;
        }

        /// <summary>是否可用,可发送
        /// </summary>
        protected override bool IsAvailable()
        {
            return _channel != null && !_channel.Active;
        }

        /// <summary>连接操作
        /// </summary>
        protected override async Task DoConnect()
        {
            _channel = ConnectionAddress.LocalEndPoint == null ? await _bootStrap.ConnectAsync(ConnectionAddress.ServerEndPoint) : await _bootStrap.ConnectAsync(ConnectionAddress.ServerEndPoint, ConnectionAddress.LocalEndPoint);

            Logger.LogInformation($"Client DoConnect! name:{Name},serverEndPoint:{_channel.RemoteAddress.ToStringAddress()},localAddress:{_channel.LocalAddress.ToStringAddress()}");
        }


        private ConnectionContext GetContext()
        {
            return _connectionContext;
        }


        /// <summary>一次的发送与接收完成
        /// </summary>
        private void SendReceiveComplete()
        {
            _connectionContext = null;
            _downloader?.WriteComplete();
        }


        /// <summary>设置返回值
        /// </summary>
        private void SetResponse(ConnectionReceiveItem receiveItem)
        {
            try
            {
                //返回为Strem,需要逐步进行解析
                if (_connectionContext.StreamResponse)
                {
                    if (receiveItem.IsChunkWriting)
                    {
                        //写入流

                        //_fs.Write(receiveItem.Body, 0, receiveItem.Body.Length);
                        //写入Body
                        _downloader.WriteBuffers(receiveItem.Body);
                        _connectionContext.WritePosition += receiveItem.Body.Length;
                        if (_connectionContext.IsWriteCompleted)
                        {
                            var response = _connectionContext.Response;
                            response.SetHeader(_connectionContext.Header);
                            _taskCompletionSource.SetResult(response);
                            //完成
                            SendReceiveComplete();
                        }
                    }
                    else
                    {
                        //文件流读取,刚读取头部
                        _downloader?.Run();
                    }
                }
                else
                {
                    var response = _connectionContext.Response;
                    response.SetHeader(receiveItem.Header);
                    response.LoadContent(Option, receiveItem.Body);
                    _taskCompletionSource.SetResult(response);
                    //完成
                    SendReceiveComplete();
                }

                //释放
                ReferenceCountUtil.SafeRelease(receiveItem);

            }
            catch (Exception ex)
            {
                Logger.LogError("接收返回信息出错! {0}", ex);
                throw;
            }
        }

        private ConnectionContext CreateContext<T>(FDFSRequest<T> request) where T : FDFSResponse, new()
        {
            var context = new ConnectionContext()
            {
                Response = new T(),
                StreamRequest = request.StreamRequest,
                StreamResponse = request.StreamResponse,
                IsChunkWriting = false,
                ReadPosition = 0,
                WritePosition = 0
            };
            return context;
        }

    }
}