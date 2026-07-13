using System.Collections.Immutable;

namespace Lunil.Workspace;

internal static class GraphAlgorithms
{
    public static ImmutableArray<LuaModuleStronglyConnectedComponent> BuildComponents(
        IReadOnlyCollection<LuaModuleIdentity> modules,
        IReadOnlyCollection<LuaModuleDependency> dependencies)
    {
        var adjacency = modules.ToDictionary(
            static module => module.Name,
            static _ => new SortedSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (var dependency in dependencies.Where(static dependency =>
                     dependency.Kind == LuaModuleDependencyKind.Static &&
                     dependency.Target is not null))
        {
            if (adjacency.TryGetValue(dependency.Source.Name, out var targets))
            {
                targets.Add(dependency.Target!.Name);
            }
        }

        var index = 0;
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var raw = new List<ImmutableArray<string>>();

        void Visit(string module)
        {
            indexes[module] = index;
            lowLinks[module] = index;
            index++;
            stack.Push(module);
            onStack.Add(module);

            foreach (var target in adjacency[module])
            {
                if (!indexes.TryGetValue(target, out var targetIndex))
                {
                    Visit(target);
                    lowLinks[module] = Math.Min(lowLinks[module], lowLinks[target]);
                }
                else if (onStack.Contains(target))
                {
                    lowLinks[module] = Math.Min(lowLinks[module], targetIndex);
                }
            }

            if (lowLinks[module] != indexes[module])
            {
                return;
            }

            var component = ImmutableArray.CreateBuilder<string>();
            string item;
            do
            {
                item = stack.Pop();
                onStack.Remove(item);
                component.Add(item);
            }
            while (!string.Equals(item, module, StringComparison.Ordinal));

            raw.Add(component.OrderBy(static name => name, StringComparer.Ordinal).ToImmutableArray());
        }

        foreach (var module in adjacency.Keys.OrderBy(static name => name, StringComparer.Ordinal))
        {
            if (!indexes.ContainsKey(module))
            {
                Visit(module);
            }
        }

        return raw
            .OrderBy(static component => component[0], StringComparer.Ordinal)
            .Select((component, id) => new LuaModuleStronglyConnectedComponent(
                id,
                [.. component.Select(static name => new LuaModuleIdentity(name))],
                component.Length > 1 || adjacency[component[0]].Contains(component[0])))
            .ToImmutableArray();
    }

    public static ImmutableArray<ImmutableArray<int>> BuildDependencyLevels(
        ImmutableArray<LuaModuleStronglyConnectedComponent> components,
        IReadOnlyCollection<LuaModuleDependency> dependencies)
    {
        var componentByModule = components.SelectMany(component => component.Modules.Select(module =>
                (module.Name, component.Id)))
            .ToDictionary(static pair => pair.Name, static pair => pair.Id, StringComparer.Ordinal);
        var componentDependencies = components.ToDictionary(
            static component => component.Id,
            static _ => new SortedSet<int>());
        var dependents = components.ToDictionary(
            static component => component.Id,
            static _ => new SortedSet<int>());
        foreach (var dependency in dependencies.Where(static dependency => dependency.Target is not null))
        {
            var source = componentByModule[dependency.Source.Name];
            var target = componentByModule[dependency.Target!.Name];
            if (source == target || !componentDependencies[source].Add(target))
            {
                continue;
            }

            dependents[target].Add(source);
        }

        var remaining = componentDependencies.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Count);
        var ready = new SortedSet<int>(remaining.Where(static pair => pair.Value == 0)
            .Select(static pair => pair.Key));
        var levels = ImmutableArray.CreateBuilder<ImmutableArray<int>>();
        var visited = 0;
        while (ready.Count != 0)
        {
            var level = ready.ToImmutableArray();
            ready.Clear();
            levels.Add(level);
            visited += level.Length;
            foreach (var component in level)
            {
                foreach (var dependent in dependents[component])
                {
                    remaining[dependent]--;
                    if (remaining[dependent] == 0)
                    {
                        ready.Add(dependent);
                    }
                }
            }
        }

        if (visited != components.Length)
        {
            throw new InvalidOperationException("The condensed module graph must be acyclic.");
        }

        return levels.ToImmutable();
    }
}
