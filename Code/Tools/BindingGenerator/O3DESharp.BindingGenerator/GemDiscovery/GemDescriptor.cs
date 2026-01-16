// GemDescriptor.cs

using System;
using System.Collections.Generic;

namespace O3DESharp.BindingGenerator.GemDiscovery
{
    public class GemDescriptor
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public List<string> Dependencies { get; set; }

        public GemDescriptor(string name, string version)
        {
            Name = name;
            Version = version;
            Dependencies = new List<string>();
        }

        public void AddDependency(string dependency)
        {
            Dependencies.Add(dependency);
        }
    }
}