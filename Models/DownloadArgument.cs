using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using MapDownloader.Attributes;

namespace MapDownloader.Models
{
    public class DownloadArgument
    {
        public string Name { get; set; }
        public bool IsReadOnly { get; set; }
        public string Width { get; set; } = "240";
        public PropertyInfo Property { get; }
        public object Instance { get; }
        public object Value { get => Property.GetValue(Instance); set => Property.SetValue(Instance, Convert.ChangeType(value, Property.PropertyType)); }

        public DownloadArgument(object instance, PropertyInfo property)
        {
            Property = property;
            Instance = instance;
        }

        public static List<DownloadArgument> GetArguments(object instance)
        {
            var type = instance.GetType();
            var props = type.GetProperties();
            var args = new List<DownloadArgument>();
            foreach (var prop in props)
            {
                var attr = Attribute.GetCustomAttribute(prop, typeof(DonwloadArgumentAttribute)) as DonwloadArgumentAttribute;
                if (attr == null)
                {
                    continue;
                }

                args.Add(new DownloadArgument(instance, prop)
                {
                    Name = attr.Name,
                    IsReadOnly = attr.IsReadOnly,
                });


            }
            return args;
        }
    }
}
