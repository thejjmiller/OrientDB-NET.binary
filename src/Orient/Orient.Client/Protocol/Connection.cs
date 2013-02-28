﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using Orient.Client.Protocol.Serializers;

namespace Orient.Client.Protocol
{
    internal class Connection
    {
        private TcpClient _socket;
        private NetworkStream _networkStream;
        private byte[] _readBuffer;

        internal string Hostname { get; set; }
        internal int Port { get; set; }
        internal bool IsReusable { get; set; }
        internal short ProtocolVersion { get; set; }
        internal int SessionId { get; private set; }
        internal bool IsActive
        {
            get
            {
                if ((_socket != null) && _socket.Connected)
                {
                    return true;
                }

                return false;
            }
        }

        internal Connection(string hostname, int port, bool isReusable)
        {
            Hostname = hostname;
            Port = port;
            IsReusable = isReusable;
            ProtocolVersion = 0;
            SessionId = -1;
        }

        internal short Initialize()
        {
            _readBuffer = new byte[1024];

            try
            {
                _socket = new TcpClient(Hostname, Port);
            }
            catch (SocketException ex)
            {
                throw new Exception(ex.Message, ex.InnerException);
            }

            _networkStream = _socket.GetStream();
            _networkStream.Read(_readBuffer, 0, 2);

            ProtocolVersion = BinarySerializer.ToShort(_readBuffer.Take(2).ToArray());

            return ProtocolVersion;
        }

        internal object ExecuteOperation<T>(T operation)
        {
            Request request = ((IOperation)operation).Request(SessionId);
            byte[] buffer;

            foreach (DataItem item in request.DataItems)
            {
                switch (item.Type)
                {
                    case "byte":
                    case "short":
                    case "int":
                    case "long":
                        Send(item.Data);
                        break;
                    case "record":
                        buffer = new byte[2 + item.Data.Length];
                        Buffer.BlockCopy(BinarySerializer.ToArray(item.Data.Length), 0, buffer, 0, 2);
                        Buffer.BlockCopy(item.Data, 0, buffer, 2, item.Data.Length);
                        Send(buffer);
                        break;
                    case "bytes":
                    case "string":
                    case "strings":
                        buffer = new byte[4 + item.Data.Length];
                        Buffer.BlockCopy(BinarySerializer.ToArray(item.Data.Length), 0, buffer, 0, 4);
                        Buffer.BlockCopy(item.Data, 0, buffer, 4, item.Data.Length);
                        Send(buffer);
                        break;
                    default:
                        break;
                }
            }

            if (request.OperationMode == OperationMode.Synchronous)
            {
                Response response = new Response();

                response.Data = Receive();
                // parse standard response fields
                response.Status = (ResponseStatus)BinarySerializer.ToByte(response.Data.Take(1).ToArray());
                response.SessionId = BinarySerializer.ToInt(response.Data.Skip(1).Take(4).ToArray());

                if (response.Status == ResponseStatus.ERROR)
                {
                    ParseResponseError(response);
                }

                return ((IOperation)operation).Response(response);
            }
            else
            {
                return null;
            }
        }

        internal void Close()
        {
            SessionId = -1;

            if ((_networkStream != null) && (_socket != null))
            {
                _networkStream.Close();
                _socket.Close();
            }

            _networkStream = null;
            _socket = null;
        }

        #region Private methods

        private void Send(byte[] rawData)
        {
            if ((_networkStream != null) && _networkStream.CanWrite)
            {
                _networkStream.Write(rawData, 0, rawData.Length);
            }
        }

        private byte[] Receive()
        {
            IEnumerable<byte> buffer = new List<byte>();

            if ((_networkStream != null) && _networkStream.CanRead)
            {
                do
                {
                    int bytesRead = _networkStream.Read(_readBuffer, 0, _readBuffer.Length);

                    buffer = buffer.Concat(_readBuffer.Take(bytesRead));
                }
                while (_networkStream.DataAvailable);
            }

            return buffer.ToArray();
        }

        private void ParseResponseError(Response response)
        {
            int offset = 5;
            string exceptionString = "";

            byte followByte = BinarySerializer.ToByte(response.Data.Skip(offset).Take(1).ToArray());
            offset += 1;

            while (followByte == 1)
            {
                int exceptionClassLength = BinarySerializer.ToInt(response.Data.Skip(offset).Take(4).ToArray());
                offset += 4;

                exceptionString += BinarySerializer.ToString(response.Data.Skip(offset).Take(exceptionClassLength).ToArray()) + ": ";
                offset += exceptionClassLength;

                int exceptionMessageLength = BinarySerializer.ToInt(response.Data.Skip(offset).Take(4).ToArray());
                offset += 4;

                // don't read exception message string if it's null
                if (exceptionMessageLength != -1)
                {
                    exceptionString += BinarySerializer.ToString(response.Data.Skip(offset).Take(exceptionMessageLength).ToArray()) + "\n";
                    offset += exceptionMessageLength;
                }

                followByte = BinarySerializer.ToByte(response.Data.Skip(offset).Take(1).ToArray());
                offset += 1;
            }

            throw new Exception(exceptionString);
        }

        #endregion
    }
}
