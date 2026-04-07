// Models/GoogleContact.cs
using System.Collections.Generic;

namespace GoogleContactSyncWP.Models
{
    public class GoogleContact
    {
        public string Id         { get; set; }
        public string ETag       { get; set; }
        public string FirstName  { get; set; }
        public string LastName   { get; set; }
        public string Nickname   { get; set; }
        public string Notes      { get; set; }
        public string UpdateTime { get; set; }

        public List<GPhone>   Phones        { get; set; }
        public List<GEmail>   Emails        { get; set; }
        public List<GAddress> Addresses     { get; set; }
        public List<GUrl>     Urls          { get; set; }
        public List<GOrg>     Organizations { get; set; }
        public GDate          Birthday      { get; set; }

        public GoogleContact()
        {
            Id = ""; ETag = ""; FirstName = ""; LastName = "";
            Nickname = ""; Notes = ""; UpdateTime = "";
            Phones        = new List<GPhone>();
            Emails        = new List<GEmail>();
            Addresses     = new List<GAddress>();
            Urls          = new List<GUrl>();
            Organizations = new List<GOrg>();
        }
    }

    public class GPhone   { public string Number  { get; set; } public string Type { get; set; } }
    public class GEmail   { public string Address { get; set; } public string Type { get; set; } }
    public class GUrl     { public string Value   { get; set; } public string Type { get; set; } }
    public class GOrg     { public string Name    { get; set; } public string Title { get; set; } }
    public class GAddress
    {
        public string Street     { get; set; }
        public string City       { get; set; }
        public string Region     { get; set; }
        public string PostalCode { get; set; }
        public string Country    { get; set; }
        public string Type       { get; set; }
    }
    public class GDate
    {
        public int?  Year  { get; set; }
        public int   Month { get; set; }
        public int   Day   { get; set; }
    }
}
