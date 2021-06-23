using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using TileDownloader.Attributes;

namespace TileDownloader.Models
{
    public class Argument
    {
        public string Name { get; set; }
        public bool IsReadOnly { get; set; }

        public PropertyInfo Property { get; }
        public object Instance { get; }
        public object Value { get => Property.GetValue(Instance); set => Property.SetValue(Instance, Convert.ChangeType(value, Property.PropertyType)); }

        public Argument(object instance, PropertyInfo property)
        {
            Property = property;
            Instance = instance;
        }

        public static List<Argument> GetArguments(object instance)
        {
            var type = instance.GetType();
            var props = type.GetProperties();
            var args = new List<Argument>();
            foreach (var prop in props)
            {
                var attr = Attribute.GetCustomAttribute(prop, typeof(ArgumentAttribute)) as ArgumentAttribute;
                if (attr == null)
                {
                    continue;
                }

                args.Add(new Argument(instance, prop)
                {
                    Name = attr.Name,
                    IsReadOnly = attr.IsReadOnly,
                });


            }
            return args;
        }
    }
}
