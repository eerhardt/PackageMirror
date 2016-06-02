using System;
using System.Collections.Generic;

namespace PackageMirror.Models
{
    public class ExtraDestination
    {
        public Uri SourceFeedUrl { get; set; }
        public Uri DestinationFeedUrl { get; set; }

        public static List<ExtraDestination> Parse(string input)
        {
            List<ExtraDestination> result = new List<ExtraDestination>();

            string[] extraDestinations = input?.Split('|') ?? new string[] { };
            foreach (string extraDestination in extraDestinations)
            {
                if (!string.IsNullOrEmpty(extraDestination))
                {
                    try
                    {
                        string[] extraDestinationParts = extraDestination.Split(new[] { "=>" }, StringSplitOptions.None);
                        result.Add(new ExtraDestination()
                        {
                            SourceFeedUrl = new Uri(extraDestinationParts[0]),
                            DestinationFeedUrl = new Uri(extraDestinationParts[1])
                        });
                    }
                    catch
                    {
                        //skip
                    }
                }
            }

            return result;
        }
    }
}