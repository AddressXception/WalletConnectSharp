namespace WalletConnectSharp.Models
{
    public class WCSessionRequestResponse : JsonRpcResponse
    {
        public WcSessionRequestResponseResult result;

        public class WcSessionRequestResponseResult
        {
            public string peerId;
            public ClientMeta peerMeta;
            public bool approved;
            public int chainId;
            public string[] accounts;
        }
    }
}