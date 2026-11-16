using CTP;

Console.WriteLine($"Process: {(Environment.Is64BitProcess ? "x64" : "x86")}");
Console.WriteLine($"MdApiVersion: {FtdcMdAdapter.GetApiVersion()}");
Console.WriteLine($"TdApiVersion: {FtdcTdAdapter.GetApiVersion()}");

using (var md = new FtdcMdAdapter("", false, false))
{
    Console.WriteLine("FtdcMdAdapter created/released");
}

using (var td = new FtdcTdAdapter(""))
{
    Console.WriteLine("FtdcTdAdapter created/released");
}
