using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PackageMirror.Models
{
    public class PackageAddedModel
    {
        public string Identifier { get; set; }
        public string PayloadType { get; set; }
        public PackageAddedPayload Payload { get; set; }
    }

    public class PackageAddedPayload
    {
        public string PackageType { get; set; }//:"NuGet",
        public string PackageIdentifier { get; set; }
        public string PackageVersion { get; set; }
        public string PackageDownloadUrl { get; set; }
        public string FeedUrl { get; set; }
    }
}
