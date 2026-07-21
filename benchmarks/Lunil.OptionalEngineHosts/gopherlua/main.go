package main

import (
	"fmt"
	"os"
	"strconv"
	"strings"
	"time"

	lua "github.com/yuin/gopher-lua"
)

func main() {
	if len(os.Args) < 4 {
		fmt.Fprintln(os.Stderr, "usage: gopherlua-host workload.lua operations warmup")
		os.Exit(2)
	}
	workloadPath := os.Args[1]
	operations, err := strconv.Atoi(os.Args[2])
	if err != nil {
		panic(err)
	}
	warmup, err := strconv.Atoi(os.Args[3])
	if err != nil {
		panic(err)
	}
	raw, err := os.ReadFile(workloadPath)
	if err != nil {
		panic(err)
	}
	source := string(raw)
	// Match NeoLua harness: force float accumulators for suite expectedPerOperation parity.
	source = strings.ReplaceAll(source, "local checksum = 0\n", "local checksum = 0.0\n")
	source = strings.ReplaceAll(source, "local sum = 0\n", "local sum = 0.0\n")
	source = strings.ReplaceAll(source, "local value = 0\n", "local value = 0.0\n")
	source = strings.ReplaceAll(source, "local total = 0\n", "local total = 0.0\n")
	// Bind operations via ARG global set by host (more reliable than chunk varargs).
	if strings.Contains(source, "local operations = ... or 1") {
		source = strings.Replace(source, "local operations = ... or 1", "local operations = ARG", 1)
	} else {
		source = "return (function(...)\n" + source + "\nend)(ARG)"
	}

	L := lua.NewState()
	defer L.Close()
	setupStart := time.Now()

	fn, err := L.LoadString(source)
	if err != nil {
		panic(err)
	}

	run := func(ops int) float64 {
		L.SetTop(0)
		L.SetGlobal("ARG", lua.LNumber(ops))
		L.Push(fn)
		if err := L.PCall(0, 1, nil); err != nil {
			panic(err)
		}
		v := L.Get(-1)
		n, ok := v.(lua.LNumber)
		if !ok {
			panic(fmt.Sprintf("expected number result, got %s (%v)", v.Type(), v))
		}
		return float64(n)
	}

	for i := 0; i < warmup; i++ {
		_ = run(1)
	}
	setup := time.Since(setupStart).Seconds()
	start := time.Now()
	result := run(operations)
	elapsed := time.Since(start).Seconds()
	fmt.Printf("cross_runtime_result\telapsed=%.17g\tsetup=%.17g\toperations=%d\tresult=%.17g\tjit_enabled=0\n",
		elapsed, setup, operations, result)
}
