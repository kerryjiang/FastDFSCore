﻿using FastDFSCore.Utility;
using System;

namespace FastDFSCore.Protocols
{
    /// <summary>上传文件返回
    /// </summary>
    public class UploadFileResp : FastDFSResp
    {
        /// <summary>组名
        /// </summary>
        public string GroupName { get; set; }

        /// <summary>文件FileId
        /// </summary>
        public string FileId { get; set; }

        /// <summary>Ctor
        /// </summary>
        public UploadFileResp()
        {

        }

        /// <summary>LoadContent
        /// </summary>
        public override void LoadContent(FastDFSOption option, byte[] data)
        {
            var dataSpan = data.AsSpan();
            var groupNameBufferSpan = dataSpan.Slice(0, Consts.FDFS_GROUP_NAME_MAX_LEN);
            GroupName = ByteUtil.ByteToString(groupNameBufferSpan.ToArray(), option.Charset).TrimEnd('\0');
            var fileNameBufferSpan = dataSpan.Slice(Consts.FDFS_GROUP_NAME_MAX_LEN);
            FileId = ByteUtil.ByteToString(fileNameBufferSpan.ToArray(), option.Charset).TrimEnd('\0');

            //byte[] groupNameBuffer = new byte[Consts.FDFS_GROUP_NAME_MAX_LEN];
            //Array.Copy(data, groupNameBuffer, Consts.FDFS_GROUP_NAME_MAX_LEN);
            //GroupName = ByteUtil.ByteToString(groupNameBuffer, option.Charset).TrimEnd('\0');

            //byte[] fileNameBuffer = new byte[data.Length - Consts.FDFS_GROUP_NAME_MAX_LEN];
            //Array.Copy(data, Consts.FDFS_GROUP_NAME_MAX_LEN, fileNameBuffer, 0, fileNameBuffer.Length);
            //FileId = ByteUtil.ByteToString(fileNameBuffer, option.Charset).TrimEnd('\0');
        }
    }
}
