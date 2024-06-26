﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using DynamicData;
using EncryptChat.ViewModels;

namespace EncryptChat.Models
{
    public class SocketConnection
    {
        private string? _ip;
        private int _port;
        private static bool _isServerStatic;
        private bool _isServer;
        private static IPHostEntry _host = default!;
        private static IPAddress _ipAddress = default!;
        private static IPEndPoint _localEndPoint = default!;
        private static IPEndPoint _remoteEP = default!;
        public static Socket? ClientSocket;
        public static Socket? ServerSocket;

        private static List<Socket> _clientSockets = new List<Socket>();
        private static object _lock = new object();
        public static MessageCryptography Crypto;

        // Dictionary to store public keys for each client IP address
        private static Dictionary<string, string> _clientPublicKeys = new Dictionary<string, string>();

        /// <summary>
        /// Initializes a new instance of the SocketConnection class as a server.
        /// </summary>
        /// <param name="isServer">Boolean indicating if this instance is a server.</param>
        public SocketConnection(bool isServer)
        {
            this._isServer = isServer;
            _isServerStatic = isServer;
            if (_isServer)
            {
                Crypto = new MessageCryptography();
                StartServer();
            }
        }

        /// <summary>
        /// Initializes a new instance of the SocketConnection class as a client.
        /// </summary>
        /// <param name="ip">The IP address to connect to.</param>
        /// <param name="port">The port to connect to.</param>
        public SocketConnection(string ip, int port)
        {
            this._ip = ip;
            this._port = port;
            Crypto = new MessageCryptography();
            StartClient();
        }

        /// <summary>
        /// Starts the server, binds to the local endpoint, and listens for incoming connections.
        /// </summary>
        private static async Task StartServer()
        {
            _host = Dns.GetHostEntry("localhost");
            _ipAddress = _host.AddressList[0];
            _localEndPoint = new IPEndPoint(_ipAddress, 11000);

            try
            {
                // Create a socket
                ServerSocket = new Socket(_ipAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                // A Socket must be associated with an endpoint using the Bind method
                ServerSocket.Bind(_localEndPoint);

                // Listen for incoming connections
                ServerSocket.Listen(10);
                MainWindowViewModel.Messages.Add("Server listening on port : 11000...");
                MainWindowViewModel.Messages.Add("Waiting for connections...");

                while (true)
                {
                    // Accept a connection
                    var handler = await ServerSocket.AcceptAsync();
                    lock (_lock)
                    {
                        _clientSockets.Add(handler);
                    }
                    MainWindowViewModel.Messages.Add("Client connected, ip: " + IPAddress.Parse(((IPEndPoint)handler.RemoteEndPoint).Address.ToString()));

                    // Handle the client in a separate task
                    await Task.Run(() => HandleClient(handler));
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }
        }

        /// <summary>
        /// Handles communication with a connected client, including receiving messages and broadcasting them to other clients.
        /// </summary>
        /// <param name="handler">The socket connected to the client.</param>
        private static async Task HandleClient(Socket handler)
        {
            try
            {
                MainWindowViewModel.Messages.Add("sending: "+ "<RSA_PUBLIC_KEY_REQUEST>");
                handler.Send(Encoding.ASCII.GetBytes("<RSA_PUBLIC_KEY_REQUEST>"));
                
                while (true)
                {
                    byte[] buffer = new byte[1024];
                    int bytesRec = await handler.ReceiveAsync(new ArraySegment<byte>(buffer), SocketFlags.None);
                    if (bytesRec > 0)
                    {
                        string data = Encoding.ASCII.GetString(buffer, 0, bytesRec);

                        // Get the sender's IP address
                        string senderIp = ((IPEndPoint)handler.RemoteEndPoint).Address.ToString();
                        string messageWithIp = $"{senderIp}: {data}";
                        MainWindowViewModel.Messages.Add(messageWithIp);

                        byte[] msg = Encoding.ASCII.GetBytes(messageWithIp);

                        lock (_lock)
                        {
                            foreach (var clientConnection in _clientSockets)
                            {
                                if (clientConnection != handler || !data.Contains("<RSA_PUBLIC_KEY>")) // Avoid echoing the message back to the sender and check if message is public key
                                {
                                    clientConnection.Send(new ArraySegment<byte>(msg), SocketFlags.None);
                                }
                                else if (data.Contains("<RSA_PUBLIC_KEY>"))
                                {
                                    MainWindowViewModel.Messages.Add(data);
                                    string publicKey = ExtractPublicKey(data);
                                    if (!string.IsNullOrEmpty(publicKey))
                                    { 
                                        _clientPublicKeys[senderIp] = publicKey;
                                    }
                                }
                            }
                        }

                        // Optionally, add or update the client's public key in the dictionary
                        // Assuming the public key is part of the data, you'll need to parse it out
                        // For example:
                    }
                }
            }
            catch (SocketException)
            {
                // Handle client disconnection
                lock (_lock)
                {
                    _clientSockets.Remove(handler);
                }
                MainWindowViewModel.Messages.Add("Client disconnected.");
                handler.Close();
            }
            catch (Exception ex)
            {
                MainWindowViewModel.Messages.Add("Error: " + ex.Message);
            }
        }

        /// <summary>
        /// Starts the client and connects to the server.
        /// </summary>
        private void StartClient()
        {
            byte[] bytes = new byte[1024];

            try
            {
                if (!IPAddress.TryParse(_ip!, out _ipAddress))
                {
                    MainWindowViewModel.Messages.Add("Invalid IP address format.");
                    return;
                }
                MainWindowViewModel.Messages.Add("Connecting to: " + _ipAddress);
                //_remoteEP = new IPEndPoint(_ipAddress, _port);
                _host = Dns.GetHostEntry(_ip);
                _remoteEP = new IPEndPoint(_ipAddress, _port);

                // Create a socket
                ClientSocket = new Socket(_ipAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                

                try
                {
                    ClientSocket.Connect(_remoteEP);
                    MainWindowViewModel.Messages.Add("Socket connected to " + ClientSocket?.RemoteEndPoint?.ToString());

                    // Start receiving messages from the server
                    Task.Run(() => ReceiveMessages());
                }
                catch (SocketException se)
                {
                    MainWindowViewModel.Messages.Add("SocketException: " + se.ToString());
                }
                catch (ArgumentNullException ane)
                {
                    MainWindowViewModel.Messages.Add("ArgumentNullException: " + ane.ToString());
                }
                catch (Exception e)
                {
                    MainWindowViewModel.Messages.Add("Unexpected exception: " + e.ToString());
                }

            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }
        }

        /// <summary>
        /// Receives messages from the server.
        /// </summary>
        private static async Task ReceiveMessages()
        {
            try
            {
                while (true)
                {
                    byte[] buffer = new byte[1024];
                    int bytesRec = await ClientSocket.ReceiveAsync(new ArraySegment<byte>(buffer), SocketFlags.None);
                    if (bytesRec > 0)
                    {
                        string data = Encoding.ASCII.GetString(buffer, 0, bytesRec);
                        if (data.Contains("<RSA_PUBLIC_KEY>") )
                        {
                            MainWindowViewModel.Messages.Add("Received" + data);
                            //TODO: Implement loading a public key
                        }
                        else if (data.Contains("<RSA_PUBLIC_KEY_REQUEST>") && !_isServerStatic)
                        {
                            MainWindowViewModel.Messages.Add("debug: Crypto.GetPublicKey()");
                            byte[] PkMsg = Encoding.ASCII.GetBytes("<RSA_PUBLIC_KEY>" + Crypto.GetPublicKey());
                            SendMessageClient(PkMsg);
                        }
                        else
                        {
                            MainWindowViewModel.Messages.Add(data); //TODO: change to decryption
                        }
                    }
                }
            }
            catch (SocketException)
            {
                MainWindowViewModel.Messages.Add("Disconnected from server.");
            }
            catch (Exception ex)
            {
                MainWindowViewModel.Messages.Add("Error: " + ex.Message);
            }
        }

        /// <summary>
        /// Sends a message from the client to the server.
        /// </summary>
        /// <param name="bytes">The message to send as a byte array.</param>
        private static void SendMessageClient(byte[] bytes)
        {
            try
            {
                // Send the data through the socket.
                int bytesSent = ClientSocket.Send(bytes);
            }
            catch (Exception ex)
            {
                MainWindowViewModel.Messages.Add("Error sending message: " + System.Text.Encoding.ASCII.GetString(bytes) + " | "+ ex.Message);
            }
        }

        /// <summary>
        /// Sends a public message from the client to the server.
        /// </summary>
        /// <param name="message">The message to send as a string.</param>
        public void SendMessageClientPublic(string message)
        {
            byte[] bytes = Encoding.ASCII.GetBytes("<MSG>" + message);
            SendMessageClient(bytes);
        }

        /// <summary>
        /// Extracts the public key from the message data.
        /// </summary>
        /// <param name="data">The message data.</param>
        /// <returns>The extracted public key as a string.</returns>
        private static string ExtractPublicKey(string data)
        {
            // Implement logic to extract public key from the data
            // For now, let's assume the public key is included in the data and can be parsed out
            // This is just an example, you need to adapt this based on your actual data format
            string[] parts = data.Split(new[] { "PUBLICKEY:" }, StringSplitOptions.None);
            if (parts.Length > 1)
            {
                return parts[1].Split(' ')[0]; // Extract the key part
            }
            return string.Empty;
        }
    }
}
