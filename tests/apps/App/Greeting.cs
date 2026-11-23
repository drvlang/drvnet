namespace App;

public static class Greeting
{
    public static string Message => Shared.Hello.Message;
    public static void Main() => System.Console.WriteLine(Message);
}
