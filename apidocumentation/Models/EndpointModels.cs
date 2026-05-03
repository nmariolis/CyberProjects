namespace Documentation.Models
{
    public class EndpointsConfig
    {
        public EndpointEntry[] Endpoints { get; set; }
    }

    public class EndpointEntry
    {
        public string Id         { get; set; }
        public string KeyHash    { get; set; }
        public string EncodedKey { get; set; }
        public string Name       { get; set; }
        public string BaseUrl    { get; set; }
    }
}
