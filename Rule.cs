using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace tidyDocuments
{
    public class Rule
    {
        public string Name { get; set; }
        public string DestinationPath { get; set; }
        public string DateFormat { get; set; }
        public Int32 DateSkip { get; set; }
        public string DateFormatTryParse { get; set; }
        public List<string> Keywords { get; set; }
        public string FilenamePattern { get; set; }
        public string CultureInfo { get; set; }

        public List<string> FoundKeywords { get; set; } = new List<string>();
    }
}
