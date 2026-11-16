using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CKBusbarBridge.models
{
    public class CurrBusBridgeItem
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public bool Visible { get; set; }

        public CurrBusBridgeItem(int id, string name, bool visible)
        {
            Id = id;
            Name = name;
            Visible = visible;
        }

        public override string ToString()
        {
            return $@"Id:{this.Id}, Name:{this.Name},Visible:{this.Visible}";
        }

        public override bool Equals(object obj)
        {
            return base.Equals(obj);
        }

        public override int GetHashCode()
        {
            return base.GetHashCode();
        }
    }
}
