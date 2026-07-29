internal static class LunilMath
{
    private const double TwoPow1023 = 8.9884656743115795e307;
    private const double TwoPowMinus969 = 2.0041683600089728e-292;

    public static double ScaleB(double value, int exponent)
    {
        var scaled = value;
        if (exponent > 1023)
        {
            scaled *= TwoPow1023;
            exponent -= 1023;
            if (exponent > 1023)
            {
                scaled *= TwoPow1023;
                exponent -= 1023;
                if (exponent > 1023)
                {
                    exponent = 1023;
                }
            }
        }
        else if (exponent < -1022)
        {
            scaled *= TwoPowMinus969;
            exponent += 969;
            if (exponent < -1022)
            {
                scaled *= TwoPowMinus969;
                exponent += 969;
                if (exponent < -1022)
                {
                    exponent = -1022;
                }
            }
        }

        var power = BitConverter.Int64BitsToDouble((long)(0x3ff + exponent) << 52);
        return scaled * power;
    }

    public static int ILogB(double value)
    {
        var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        var exponent = (int)((bits >> 52) & 0x7ff);
        if (exponent == 0x7ff)
        {
            return int.MaxValue;
        }

        if (exponent != 0)
        {
            return exponent - 1023;
        }

        var significand = bits & 0x000f_ffff_ffff_ffff;
        if (significand == 0)
        {
            return int.MinValue;
        }

        var result = -1074;
        while ((significand >>= 1) != 0)
        {
            result++;
        }

        return result;
    }

    public static double Log2(double value) => Math.Log(value) / Math.Log(2.0);
}
