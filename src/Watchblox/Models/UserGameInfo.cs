namespace Watchblox.Models
{
    /// <summary>A game on a user's profile: their own creation or a favorite.</summary>
    public class UserGameInfo
    {
        public long UniverseId { get; set; }
        public string Name { get; set; } = "";
        public long? PlaceId { get; set; }
        public long PlaceVisits { get; set; }
    }
}
