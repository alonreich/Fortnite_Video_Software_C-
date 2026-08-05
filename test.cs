using System;

class Program
{
    static void Main()
    {
        try
        {
            Console.WriteLine(Math.Clamp(10, 20, 5));
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
        }
    }
}
