namespace MyApiTemplate.Utils
{
    public class GlobalConfig
    {
        public string? ConnectionString { get; }

        public GlobalConfig(IConfiguration configuration)
        {
            ConnectionString = configuration["ConnectionStrings:Default"];
        }
    }
}
