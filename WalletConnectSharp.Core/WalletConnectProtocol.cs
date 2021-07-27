﻿using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Newtonsoft.Json;
using WalletConnectSharp.Core.Events;
using WalletConnectSharp.Core.Events.Request;
using WalletConnectSharp.Core.Events.Response;
using WalletConnectSharp.Core.Models;
using WalletConnectSharp.Core.Network;

namespace WalletConnectSharp.Core
{
    public class WalletConnectProtocol : IDisposable
    {
        public static readonly string[] SigningMethods = new[]
        {
            "eth_sendTransaction",
            "eth_signTransaction",
            "eth_sign",
            "eth_signTypedData",
            "eth_signTypedData_v1",
            "eth_signTypedData_v2",
            "eth_signTypedData_v3",
            "eth_signTypedData_v4",
            "personal_sign",
        };

        private string clientId = "";
        private readonly string _handshakeTopic;
        public readonly EventDelegator Events;

        public event EventHandler<WalletConnectProtocol> OnConnect;
        public event EventHandler<WalletConnectProtocol> OnDisconnect;
        
        private long _handshakeId;
        private const string Version = "1";
        private readonly string _bridgeUrl;
        private string _key;
        private byte[] _keyRaw;
        private string peerId;
        
        public int? NetworkId { get; private set; }

        public bool Connected { get; private set; }

        public string[] Accounts { get; private set; }

        public int? ChainId { get; private set; }

        public ClientMeta ClientMetadata { get; set; }

        public ITransport Transport { get; private set; }

        public ICipher Cipher { get; private set; }

        public WalletConnectProtocol(ClientMeta clientMeta, ITransport transport = null,
            ICipher cipher = null,
            int? chainId = 1,
            string bridgeUrl = null,
            EventDelegator eventDelegator = null
        )
        {
            if (clientMeta == null)
            {
                throw new ArgumentException("clientMeta cannot be null!");
            }

            if (string.IsNullOrWhiteSpace(clientMeta.Description))
            {
                throw new ArgumentException("clientMeta must include a valid Description");
            }
            
            if (string.IsNullOrWhiteSpace(clientMeta.Name))
            {
                throw new ArgumentException("clientMeta must include a valid Name");
            }
            
            if (string.IsNullOrWhiteSpace(clientMeta.URL))
            {
                throw new ArgumentException("clientMeta must include a valid URL");
            }
            
            if (clientMeta.Icons == null || clientMeta.Icons.Length == 0)
            {
                throw new ArgumentException("clientMeta must include an array of Icons the Wallet app can use. These Icons must be URLs to images. You must include at least one image URL to use");
            }

            if (bridgeUrl == null)
            {
                bridgeUrl = DefaultBridge.ChooseRandomBridge();
            }
            
            if (eventDelegator == null)
                eventDelegator = new EventDelegator();

            this.Events = eventDelegator;

            this.ClientMetadata = clientMeta;
            this.ChainId = chainId;

            if (bridgeUrl.StartsWith("https"))
                bridgeUrl = bridgeUrl.Replace("https", "wss");
            else if (bridgeUrl.StartsWith("http"))
                bridgeUrl = bridgeUrl.Replace("http", "ws");

            var topicGuid = Guid.NewGuid();

            _handshakeTopic = topicGuid.ToString();

            clientId = Guid.NewGuid().ToString();

            if (transport == null)
                transport = TransportFactory.Instance.BuildDefaultTransport(eventDelegator);

            this._bridgeUrl = bridgeUrl;
            this.Transport = transport;

            if (cipher == null)
                cipher = new AESCipher();

            this.Cipher = cipher;

            GenerateKey();
        }

        private void GenerateKey()
        {
            //Generate a random secret
            byte[] secret = new byte[32];
            RNGCryptoServiceProvider rngCsp = new RNGCryptoServiceProvider();
            rngCsp.GetBytes(secret);

            this._keyRaw = secret;

            //Convert hex 
            this._key = BitConverter.ToString(secret).Replace("-", "").ToLower();
        }

        public string URI
        {
            get
            {
                var topicEncode = WebUtility.UrlEncode(_handshakeTopic);
                var versionEncode = WebUtility.UrlEncode(Version);
                var bridgeUrlEncode = WebUtility.UrlEncode(_bridgeUrl);
                var keyEncoded = WebUtility.UrlEncode(_key);

                return "wc:" + topicEncode + "@" + versionEncode + "?bridge=" + bridgeUrlEncode + "&key=" + keyEncoded;
            }
        }

        public async Task<WCSessionData> Connect()
        {
            Transport.MessageReceived += TransportOnMessageReceived;

            await Transport.Open(this._bridgeUrl);

            await Transport.Subscribe(this.clientId);

            var result = await CreateSession();
            
            if (OnConnect != null)
                OnConnect(this, this);

            return result;
        }

        public async void Disconnect(string disconnectMessage = "Session Disconnected")
        {
            //A blank WCSessionData will make a fields null
            var request = new WCSessionUpdate(new WCSessionData());

            await SendRequest(request);

            HandleSessionDisconnect(disconnectMessage);
        }

        /// <summary>
        /// Create a new WalletConnect session with a Wallet.
        /// </summary>
        /// <returns></returns>
        private async Task<WCSessionData> CreateSession()
        {
            var data = new WcSessionRequestRequest(ClientMetadata, clientId, ChainId);

            this._handshakeId = data.ID;

            await SendRequest(data, this._handshakeTopic);

            TaskCompletionSource<WCSessionData> eventCompleted =
                new TaskCompletionSource<WCSessionData>(TaskCreationOptions.None);

            //Listen for the _handshakeId response
            //The response will be of type WCSessionRequestResponse
            Events.ListenForResponse<WCSessionRequestResponse>(this._handshakeId, HandleSessionResponse);

            //Listen for the "connect" event triggered by 'HandleSessionResponse' above
            //This will have the type WCSessionData
            Events.ListenFor<WCSessionData>("connect",
                (sender, @event) =>
                {
                    eventCompleted.TrySetResult(@event.Response);
                });
            
            //Listen for the "session_failed" event triggered by 'HandleSessionResponse' above
            //This will have the type failure reason
            Events.ListenFor<ErrorResponse>("session_failed",
                delegate(object sender, GenericEvent<ErrorResponse> @event)
                {
                    if (@event.Response.Message == "Not Approved" || @event.Response.Message == "Session Rejected")
                    {
                        eventCompleted.TrySetCanceled();
                    }
                    else
                    {
                        eventCompleted.TrySetException(
                            new IOException("WalletConnect: Session Failed: " + @event.Response.Message));
                    }
                });

            var response = await eventCompleted.Task;

            return response;
        }

        private void HandleSessionResponse(object sender, JsonRpcResponseEvent<WCSessionRequestResponse> jsonresponse)
        {
            var response = jsonresponse.Response.result;

            if (response != null && response.approved)
            {
                bool wasConnected = Connected;

                //We are now connected
                Connected = true;

                ChainId = response.chainId;

                Accounts = response.accounts;

                if (!wasConnected)
                {
                    peerId = response.peerId;

                    ClientMetadata = response.peerMeta;

                    Events.Trigger("connect", response);
                }
                else
                {
                    Events.Trigger("session_update", response);
                }
            }
            else if (jsonresponse.Response.IsError)
            {
                HandleSessionDisconnect(jsonresponse.Response.Error.Message, "session_failed");
            }
            else
            {
                HandleSessionDisconnect("Not Approved", "session_failed");
            }
        }

        private void HandleSessionDisconnect(string msg, string topic = "disconnect")
        {
            Connected = false;

            Events.Trigger(topic, new ErrorResponse(msg));

            Transport.Close();
        }

        private async void TransportOnMessageReceived(object sender, MessageReceivedEventArgs e)
        {
            string[] activeTopics = new[] {this.clientId, _handshakeTopic};

            var networkMessage = e.Message;

            if (!activeTopics.Contains(networkMessage.Topic))
                return;

            var encryptedPayload = JsonConvert.DeserializeObject<EncryptedPayload>(networkMessage.Payload);

            var json = await Cipher.DecryptWithKey(_keyRaw, encryptedPayload);

            var response = JsonConvert.DeserializeObject<JsonRpcResponse>(json);

            //TODO Handle this case better, how to differentiate between Response and Request Object?
            if (response.Event != null)
                Events.Trigger(response.Event, json);
        }

        public async Task SendRequest<T>(T requestObject, string sendingTopic = null, bool silent = false)
        {
            string json = JsonConvert.SerializeObject(requestObject);

            var encrypted = await Cipher.EncryptWithKey(_keyRaw, json);

            if (sendingTopic == null)
                sendingTopic = peerId;

            var message = new NetworkMessage()
            {
                Payload = JsonConvert.SerializeObject(encrypted),
                Silent = silent,
                Topic = sendingTopic,
                Type = "pub"
            };

            await this.Transport.SendMessage(message);
        }

        private async Task SendRequest(JsonRpcRequest requestObject, string sendingTopic = null,
            bool? forcePushNotification = null)
        {
            bool silent;
            if (forcePushNotification != null)
            {
                silent = (bool) !forcePushNotification;
            }
            else
            {
                silent = requestObject.Method.StartsWith("wc_") || !SigningMethods.Contains(requestObject.Method);
            }

            await SendRequest(requestObject, sendingTopic, silent);
        }

        public void Dispose()
        {
            if (Transport != null)
            {
                Transport.Dispose();
                Transport = null;
            }
        }

        public async Task Disconnect()
        {
            var request = new WCSessionUpdate(new WCSessionData()
            {
                approved = false,
                chainId = null,
                accounts = null,
                networkId = null
            });

            await SendRequest(request);

            await Transport.Close();
            
            if (OnDisconnect != null)
                OnDisconnect(this, this);
        }
    }
}