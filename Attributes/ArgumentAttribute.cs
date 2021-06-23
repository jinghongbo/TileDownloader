using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TileDownloader.Attributes
{
    public class ArgumentAttribute : Attribute
    {
        public string Name { get; set; }
        public bool IsReadOnly { get; set; }

        public ArgumentAttribute(string name)
        {
            Name = name;
        }
    }
}
