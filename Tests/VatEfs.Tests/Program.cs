int passed = 0;
InstallationTests.Run((name, action) =>
{
    action();
    passed++;
    Console.WriteLine("PASS " + name);
});
Console.WriteLine($"{passed} VatEFS installation-detection checks passed. Synthetic registrations and file inventories only.");
