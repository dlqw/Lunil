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

        var reverse = modules.ToDictionary(
            static module => module.Name,
            static _ => new SortedSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (var pair in adjacency)
        {
            foreach (var target in pair.Value)
            {
                reverse[target].Add(pair.Key);
            }
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var finishOrder = new List<string>(adjacency.Count);
        foreach (var root in adjacency.Keys.OrderBy(static name => name, StringComparer.Ordinal))
        {
            if (!visited.Add(root))
            {
                continue;
            }

            var stack = new Stack<(string Module, IEnumerator<string> Targets)>();
            stack.Push((root, adjacency[root].GetEnumerator()));
            while (stack.Count != 0)
            {
                var frame = stack.Peek();
                if (frame.Targets.MoveNext())
                {
                    var target = frame.Targets.Current;
                    if (visited.Add(target))
                    {
                        stack.Push((target, adjacency[target].GetEnumerator()));
                    }

                    continue;
                }

                frame.Targets.Dispose();
                stack.Pop();
                finishOrder.Add(frame.Module);
            }
        }

        visited.Clear();
        var raw = new List<ImmutableArray<string>>();
        for (var orderIndex = finishOrder.Count - 1; orderIndex >= 0; orderIndex--)
        {
            var root = finishOrder[orderIndex];
            if (!visited.Add(root))
            {
                continue;
            }

            var component = ImmutableArray.CreateBuilder<string>();
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count != 0)
            {
                var module = stack.Pop();
                component.Add(module);
                foreach (var source in reverse[module].Reverse())
                {
                    if (visited.Add(source))
                    {
                        stack.Push(source);
                    }
                }
            }

            raw.Add(component.OrderBy(static name => name, StringComparer.Ordinal).ToImmutableArray());
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
