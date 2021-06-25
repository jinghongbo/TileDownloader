using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MapDownloader.Attributes
{
    public class DonwloadArgumentAttribute : Attribute
    {
        public string Name { get; set; }
        public bool IsReadOnly { get; set; }

        public DonwloadArgumentAttribute(string name)
        {
            Name = name;
        }
    }
}
