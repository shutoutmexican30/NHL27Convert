using Suite.Convert27;
using Suite.Core;

// Rebuilds the named mod straight from the saved Convert 27 project, without opening the app.
// Same pipeline as the "Add all matched folders" button; the merge in AddToModListAsync keeps
// any mod entries whose slots are not in the project.
var name = args.Length > 0 ? args[0] : "NHL 27 uniforms";
var vm = new Convert27ViewModel(SuiteSettings.Load());
vm.PropertyChanged += (_, e) =>
{
    if (e.PropertyName == nameof(Convert27ViewModel.Status))
        Console.WriteLine(vm.Status);
};
await vm.LoadAsync();
if (vm.Catalog27 is null || vm.Catalog24 is null)
{
    Console.Error.WriteLine("Load failed: " + vm.Status);
    return 1;
}
try
{
    var path = await vm.AddToModListAsync(name, currentFolderOnly: false);
    Console.WriteLine("Done: " + path);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
