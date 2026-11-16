using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace CKBusbarBridge.Prototype
{
    // The parameter input's reflection, serialization and subscriptions belong to the unprotected UI assembly.
    internal sealed class BridgeParameterSnapshotBinding : IDisposable
    {
        private readonly object _source;
        private readonly List<INotifyPropertyChanged> _subscriptions = new List<INotifyPropertyChanged>();
        internal event Action<JObject> Changed;
        internal BridgeParameterSnapshotBinding(object source) { _source = source; Subscribe(); }
        internal static JObject Capture(object parameters)
        {
            var serializer = JsonSerializer.Create(new JsonSerializerSettings { Culture = CultureInfo.InvariantCulture,
                Converters = { new StringEnumConverter() }, ReferenceLoopHandling = ReferenceLoopHandling.Error });
            return SinglePrecision.Copy(JObject.FromObject(parameters, serializer));
        }
        private void Subscribe()
        {
            Dispose();
            Visit(_source, new HashSet<object>());
        }
        private void Visit(object value, HashSet<object> visited)
        {
            if (value == null || value is string || value.GetType().IsValueType || !visited.Add(value)) return;
            if (value is INotifyPropertyChanged changed) { _subscriptions.Add(changed); changed.PropertyChanged += OnChanged; }
            foreach (var property in value.GetType().GetProperties().Where(item => item.CanRead && item.GetIndexParameters().Length == 0 &&
                item.PropertyType.Namespace?.StartsWith("CKBusbarBridge", StringComparison.Ordinal) == true)) Visit(property.GetValue(value, null), visited);
        }
        private void OnChanged(object sender, PropertyChangedEventArgs args)
        { Subscribe(); Changed?.Invoke(Capture(_source)); }
        public void Dispose() { foreach (var subscription in _subscriptions) subscription.PropertyChanged -= OnChanged; _subscriptions.Clear(); }
    }
}
