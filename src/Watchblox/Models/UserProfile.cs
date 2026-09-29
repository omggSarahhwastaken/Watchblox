using System;

namespace Watchblox.Models
{
    public class UserProfile
    {
        public long Id;
        public string Username = "";
        public string DisplayName = "";
        public string Description = "";
        public DateTime Created;
        public bool HasVerifiedBadge;
    }

    public class UserGroupInfo
    {
        public long GroupId;
        public string GroupName = "";
        public string RoleName = "";
        public int Rank;
        public int MemberCount;
    }
}
