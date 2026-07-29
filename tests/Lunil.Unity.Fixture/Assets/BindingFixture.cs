using Lunil.Hosting;
using System;
using System.Collections.Generic;

[assembly: LuaClrGenerateBinding(
    typeof(Lunil.Unity.Fixture.UnityBindingTarget),
    nameof(Lunil.Unity.Fixture.UnityBindingTarget.Value),
    nameof(Lunil.Unity.Fixture.UnityBindingTarget.Add),
    nameof(Lunil.Unity.Fixture.UnityBindingTarget.Raise),
    nameof(Lunil.Unity.Fixture.UnityBindingTarget.Changed))]
[assembly: LuaClrGenerateBinding(typeof(Lunil.Unity.Fixture.UnitySignalHandler))]
[assembly: LuaClrGenerateBinding(typeof(Func<int, int>))]
[assembly: LuaClrGenerateBinding(typeof(List<int>))]

namespace Lunil.Unity.Fixture
{
    public delegate void UnitySignalHandler(int value);

    public sealed class UnityBindingTarget
    {
        public UnityBindingTarget(int value)
        {
            Value = value;
        }

        public int Value { get; private set; }

        public event UnitySignalHandler Changed;

        public int Add(int left, int right)
        {
            return left + right;
        }

        public void Raise(int value)
        {
            Value = value;
            var handler = Changed;
            if (handler != null) handler(value);
        }
    }
}
