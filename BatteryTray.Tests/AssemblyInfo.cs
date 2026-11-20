using Xunit;

// Les tests redirigent le dossier de données (AppSettings.DataDir, un état statique) : on les exécute
// l'un après l'autre pour qu'ils ne se marchent pas dessus.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
