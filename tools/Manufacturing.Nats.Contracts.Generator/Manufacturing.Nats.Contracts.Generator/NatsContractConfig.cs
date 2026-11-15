namespace Manufacturing.Nats.Contracts.Generator;

public class NatsContractConfig
{
    public class SubjectConfig
    {
        public string Subject { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string? ReplyModel { get; set; }
        public string? QueueGroup { get; set; }
    }

    public class StreamConfig
    {
        public string Name { get; set; } = string.Empty;
        public string Retention { get; set; } = string.Empty;
        public Dictionary<string, SubjectConfig> Subjects { get; set; } = [];
        public List<ConsumerConfig> Consumers { get; set; } = [];
    }

    public class ConsumerConfig
    {
        public string DurableName { get; set; } = string.Empty;
        public string? ForService { get; set; }
        public List<string> FilterSubjects { get; set; } = [];
    }

    public class StreamsConfig
    {
        public StreamConfig Commands { get; set; } = new();
        public StreamConfig Events { get; set; } = new();
    }

    public class Service
    {
        public StreamsConfig Streams { get; set; } = new();
        public Dictionary<string, SubjectConfig> CoreSubjects { get; set; } = [];
    }
    
    public string BaseSubject { get; set; } = string.Empty;
    public Dictionary<string, Service> Services { get; set; } = new();
}
