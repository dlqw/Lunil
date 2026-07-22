using System.Collections.Immutable;

namespace Lunil.Hosting;

public sealed record LuaPatchDependencyComponent(
    int Id,
    ImmutableArray<string> Modules,
    bool IsCyclic);

public sealed record LuaPatchDependencyPlan(
    ImmutableArray<LuaPatchDependencyComponent> Components)
{
    public static LuaPatchDependencyPlan Create(IEnumerable<LuaPatchEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var modules = new Dictionary<string, LuaPatchEntry>(StringComparer.Ordinal);
        foreach (var entry in entries.Where(static entry =>
            entry.Kind != LuaPatchEntryKind.CompanionData))
        {
            if (string.IsNullOrWhiteSpace(entry.ModuleName))
            {
                throw new LuaPatchFormatException(
                    LuaPatchErrorCode.InvalidManifest,
                    "Every Lua patch entry must declare a module name.");
            }

            if (!modules.TryAdd(entry.ModuleName, entry))
            {
                throw new LuaPatchFormatException(
                    LuaPatchErrorCode.DuplicateModule,
                    $"Duplicate patch module '{entry.ModuleName}'.");
            }
        }

        var edges = modules.Keys.ToDictionary(
            static name => name,
            static _ => new SortedSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (var pair in modules)
        {
            var dependencies = pair.Value.Dependencies.IsDefault
                ? ImmutableArray<LuaPatchDependency>.Empty
                : pair.Value.Dependencies;
            foreach (var dependency in dependencies)
            {
                if (modules.ContainsKey(dependency.ModuleName))
                {
                    edges[pair.Key].Add(dependency.ModuleName);
                }
                else if (dependency.Kind == LuaPatchDependencyKind.Required)
                {
                    throw new LuaPatchFormatException(
                        LuaPatchErrorCode.MissingDependency,
                        $"Patch module '{pair.Key}' requires missing module '{dependency.ModuleName}'.");
                }
            }
        }

        var components = BuildStronglyConnectedComponents(edges);
        var componentByModule = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < components.Count; index++)
        {
            foreach (var module in components[index])
            {
                componentByModule.Add(module, index);
            }
        }

        var dependents = Enumerable.Range(0, components.Count)
            .ToDictionary(static index => index, static _ => new SortedSet<int>());
        var indegrees = new int[components.Count];
        foreach (var pair in edges)
        {
            var dependentComponent = componentByModule[pair.Key];
            foreach (var dependency in pair.Value)
            {
                var dependencyComponent = componentByModule[dependency];
                if (dependentComponent != dependencyComponent &&
                    dependents[dependencyComponent].Add(dependentComponent))
                {
                    indegrees[dependentComponent]++;
                }
            }
        }

        var ready = new SortedSet<int>(Comparer<int>.Create((left, right) =>
        {
            var comparison = string.CompareOrdinal(components[left][0], components[right][0]);
            return comparison != 0 ? comparison : left.CompareTo(right);
        }));
        for (var index = 0; index < indegrees.Length; index++)
        {
            if (indegrees[index] == 0)
            {
                ready.Add(index);
            }
        }

        var ordered = ImmutableArray.CreateBuilder<LuaPatchDependencyComponent>(components.Count);
        while (ready.Count != 0)
        {
            var componentIndex = ready.Min;
            ready.Remove(componentIndex);
            var component = components[componentIndex];
            var cyclic = component.Count > 1 || edges[component[0]].Contains(component[0]);
            ordered.Add(new LuaPatchDependencyComponent(
                ordered.Count,
                component.ToImmutableArray(),
                cyclic));
            foreach (var dependent in dependents[componentIndex])
            {
                if (--indegrees[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        return new LuaPatchDependencyPlan(ordered.ToImmutable());
    }

    private static List<List<string>> BuildStronglyConnectedComponents(
        Dictionary<string, SortedSet<string>> edges)
    {
        var index = 0;
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<List<string>>();

        void Visit(string module)
        {
            indices[module] = index;
            lowLinks[module] = index++;
            stack.Push(module);
            onStack.Add(module);

            foreach (var dependency in edges[module])
            {
                if (!indices.TryGetValue(dependency, out var dependencyIndex))
                {
                    Visit(dependency);
                    lowLinks[module] = Math.Min(lowLinks[module], lowLinks[dependency]);
                }
                else if (onStack.Contains(dependency))
                {
                    lowLinks[module] = Math.Min(lowLinks[module], dependencyIndex);
                }
            }

            if (lowLinks[module] != indices[module])
            {
                return;
            }

            var component = new List<string>();
            string current;
            do
            {
                current = stack.Pop();
                onStack.Remove(current);
                component.Add(current);
            }
            while (!string.Equals(current, module, StringComparison.Ordinal));
            component.Sort(StringComparer.Ordinal);
            result.Add(component);
        }

        foreach (var module in edges.Keys.Order(StringComparer.Ordinal))
        {
            if (!indices.ContainsKey(module))
            {
                Visit(module);
            }
        }

        return result;
    }
}
