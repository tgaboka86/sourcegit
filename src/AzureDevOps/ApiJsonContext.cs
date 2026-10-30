using System.Text.Json.Serialization;

namespace SourceGit.AzureDevOps
{
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(RepositoryInfo))]
    [JsonSerializable(typeof(ListResult<GitRef>))]
    [JsonSerializable(typeof(ListResult<Identity>))]
    [JsonSerializable(typeof(ListResult<PullRequest>))]
    [JsonSerializable(typeof(PullRequest))]
    [JsonSerializable(typeof(Identity))]
    [JsonSerializable(typeof(CreateRequest))]
    [JsonSerializable(typeof(UpdateRequest))]
    [JsonSerializable(typeof(ReviewerSelection))]
    public partial class ApiJsonContext : JsonSerializerContext { }
}
