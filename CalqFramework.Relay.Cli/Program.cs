using CalqFramework.Relay;

try {
    object? result = new CommandLineInterface().Execute(new RelayManager());
    switch (result) {
        case ValueTuple:
            break;
        case string str:
            Console.WriteLine(str);
            break;
        case object obj:
            Console.WriteLine(JsonSerializer.Serialize(obj));
            break;
    }
} catch (CliException ex) {
    Console.Error.WriteLine(ex.Message);
    Environment.Exit(1);
} catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException != null) {
    Console.Error.WriteLine(ex.InnerException.Message);
    Environment.Exit(1);
} catch (Exception ex) {
    Console.Error.WriteLine(ex.Message);
    Environment.Exit(1);
}
