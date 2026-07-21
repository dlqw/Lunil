namespace UniLua.Tools
{
	public class ULDebug
	{
		public static System.Action<object> Log = NoAction;
		public static System.Action<object> LogError = NoAction;
		private static void NoAction(object msg) { }
	}
}
