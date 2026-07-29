#if NET9_0_OR_GREATER
global using LunilLock = System.Threading.Lock;
#else
global using LunilLock = System.Object;
#endif
