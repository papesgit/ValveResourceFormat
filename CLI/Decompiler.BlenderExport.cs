using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamDatabase.ValvePak;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.Serialization.KeyValues;
using ValveResourceFormat.ThirdParty;
using ValveResourceFormat.TextureDecoders;
using ValveResourceFormat.Utils;
using EntityLump = ValveResourceFormat.ResourceTypes.EntityLump;
using VMaterial = ValveResourceFormat.ResourceTypes.Material;
using VMesh = ValveResourceFormat.ResourceTypes.Mesh;
using VModel = ValveResourceFormat.ResourceTypes.Model;
using VTexture = ValveResourceFormat.ResourceTypes.Texture;
using VWorld = ValveResourceFormat.ResourceTypes.World;
using VWorldNode = ValveResourceFormat.ResourceTypes.WorldNode;

namespace CLI;

public partial class Decompiler
{
    private const int BlenderPackageVersion = 1;
    private static readonly JsonSerializerOptions BlenderPackageJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static int ExportBlenderMapPackage(string mapVpkPath, string outputPath)
    {
        if (!File.Exists(mapVpkPath))
        {
            Console.Error.WriteLine($"Map VPK \"{mapVpkPath}\" does not exist.");
            return 1;
        }

        Directory.CreateDirectory(outputPath);
        Directory.CreateDirectory(Path.Combine(outputPath, "meshes"));
        Directory.CreateDirectory(Path.Combine(outputPath, "textures"));
        Directory.CreateDirectory(Path.Combine(outputPath, "materials"));

        using var package = new Package();
        package.SetFileName(mapVpkPath);

        try
        {
            package.Read(mapVpkPath);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Failed to open map VPK \"{mapVpkPath}\": {e.Message}");
            return 1;
        }

        if (package.Entries == null)
        {
            Console.Error.WriteLine($"Map VPK \"{mapVpkPath}\" did not contain an entry manifest.");
            return 1;
        }

        var allEntries = package.Entries
            .SelectMany(static group => group.Value)
            .Select(static entry => entry.GetFullPath())
            .Order(StringComparer.Ordinal)
            .ToArray();

        var mapResources = allEntries
            .Where(static path => path.EndsWith(".vmap_c", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".vwrld_c", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".vwnod_c", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        using var fileLoader = new GameFileLoader(package, mapVpkPath);
        var mainMapResource = mapResources.FirstOrDefault(static path => path.EndsWith(".vmap_c", StringComparison.OrdinalIgnoreCase));

        var manifest = new BlenderMapManifest
        {
            Version = BlenderPackageVersion,
            Generator = "ValveResourceFormat CLI",
            MapVpk = Path.GetFullPath(mapVpkPath),
            MapName = Path.GetFileNameWithoutExtension(mapVpkPath),
            MainMapResource = mainMapResource,
            ResourceCounts = package.Entries
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.Value.Count, StringComparer.Ordinal),
            MapResources = mapResources,
            Status = "scaffold",
            Notes =
            [
                "This is the first VRF Blender package handshake.",
                "Mesh, material, and texture payload export will be added next."
            ],
        };

        if (mainMapResource != null)
        {
            try
            {
                using var resource = fileLoader.LoadFile(mainMapResource);
                manifest.MainMapResourceType = resource?.ResourceType.ToString();
            }
            catch (Exception e)
            {
                manifest.MainMapLoadError = e.Message;
            }
        }

        var worldResourcePath = mapResources.FirstOrDefault(static path => path.EndsWith(".vwrld_c", StringComparison.OrdinalIgnoreCase));
        if (worldResourcePath != null)
        {
            try
            {
                ExportBlenderWorldGeometry(fileLoader, worldResourcePath, outputPath, manifest);
                using var worldResource = fileLoader.LoadFile(worldResourcePath);
                if (worldResource?.DataBlock is VWorld world)
                {
                    ExportBlenderSkybox(fileLoader, world, outputPath, manifest);
                }
                ExportBlenderMaterials(fileLoader, outputPath, manifest);
            }
            catch (Exception e)
            {
                manifest.GeometryExportError = e.Message;
            }
        }

        var manifestPath = Path.Combine(outputPath, "scene.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, BlenderPackageJsonOptions));

        Console.WriteLine($"--- Blender package written: {manifestPath}");
        Console.WriteLine($"--- Map resources discovered: {mapResources.Length}");
        Console.WriteLine($"--- Blender meshes exported: {manifest.Meshes.Count}");
        Console.WriteLine($"--- Blender objects exported: {manifest.Objects.Count}");
        Console.WriteLine($"--- Blender materials exported: {manifest.Materials.Count}");
        Console.WriteLine($"--- Blender textures exported: {manifest.ExportedTextureCount}");
        return 0;
    }

    private static void ExportBlenderWorldGeometry(
        GameFileLoader fileLoader,
        string worldResourcePath,
        string outputPath,
        BlenderMapManifest manifest,
        Matrix4x4? objectTransform = null,
        bool isSkybox = false)
    {
        using var worldResource = fileLoader.LoadFile(worldResourcePath);
        if (worldResource?.DataBlock is not VWorld world)
        {
            manifest.GeometryExportError = $"Could not load world resource {worldResourcePath}.";
            return;
        }

        if (!isSkybox)
        {
            ExportBlenderWorldLighting(fileLoader, world, outputPath, manifest);
        }

        var meshCache = new Dictionary<string, int>(StringComparer.Ordinal);
        var meshesDirectory = Path.Combine(outputPath, "meshes");
        var placementTransform = objectTransform ?? Matrix4x4.Identity;

        foreach (var worldNodeName in world.GetWorldNodeNames())
        {
            if (string.IsNullOrEmpty(worldNodeName))
            {
                continue;
            }

            using var worldNodeResource = fileLoader.LoadFile(worldNodeName + ".vwnod_c");
            if (worldNodeResource?.DataBlock is not VWorldNode worldNode)
            {
                continue;
            }

            foreach (var sceneObject in worldNode.SceneObjects)
            {
                ExportBlenderSceneObject(fileLoader, meshesDirectory, manifest, meshCache, sceneObject, placementTransform, isSkybox);
            }

            foreach (var aggregateSceneObject in worldNode.AggregateSceneObjects)
            {
                if (!ExportBlenderAggregateSceneObject(fileLoader, meshesDirectory, manifest, meshCache, aggregateSceneObject, placementTransform, isSkybox))
                {
                    manifest.UnsupportedAggregateSceneObjects++;
                }
            }

            manifest.UnsupportedClutterSceneObjects += worldNode.ClutterSceneObjects.Count;
        }

        manifest.Status = manifest.Meshes.Count > 0 ? "geometry" : "scaffold";
        manifest.Notes =
        [
            "Exports static world-node scene objects as mesh payloads.",
            "Aggregate scene objects are exported as draw-call fragment meshes.",
            "Clutter, entity-lump, texture, and full material payloads are not exported yet."
        ];
    }

    private static void ExportBlenderSkybox(GameFileLoader fileLoader, VWorld world, string outputPath, BlenderMapManifest manifest)
    {
        var skyboxReference = FindBlenderEntity(world, fileLoader, "skybox_reference");
        if (skyboxReference == null)
        {
            return;
        }

        var targetMapName = skyboxReference.GetStringProperty("targetmapname");
        if (string.IsNullOrWhiteSpace(targetMapName) || !targetMapName.EndsWith(".vmap", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var targetVpk = Path.ChangeExtension(targetMapName, ".vpk");
        Package? addedSkyboxPackage = null;
        var foundVpk = fileLoader.FindFile(targetVpk, logNotFound: false);
        if (foundVpk.PathOnDisk != null)
        {
            addedSkyboxPackage = fileLoader.AddPackageToSearch(foundVpk.PathOnDisk);
        }
        else if (foundVpk.PackageEntry != null)
        {
            var package = new Package();
            try
            {
                package.SetFileName(foundVpk.PackageEntry.GetFullPath());
                package.OptimizeEntriesForBinarySearch(StringComparison.OrdinalIgnoreCase);
                using var stream = GameFileLoader.GetPackageEntryStream(foundVpk.Package!, foundVpk.PackageEntry);
                package.Read(stream);
                fileLoader.AddPackageToSearch(package);
                addedSkyboxPackage = package;
                package = null;
            }
            finally
            {
                package?.Dispose();
            }
        }

        if (addedSkyboxPackage == null)
        {
            return;
        }

        try
        {
            var skyboxWorldPath = Path.Join(
                Path.GetDirectoryName(targetMapName),
                Path.GetFileNameWithoutExtension(targetMapName),
                "world.vwrld_c"
            ).Replace('\\', '/');

            using var skyboxWorldResource = fileLoader.LoadFile(skyboxWorldPath);
            if (skyboxWorldResource?.DataBlock is not VWorld skyboxWorld)
            {
                manifest.Skybox = new BlenderSkyboxInfo
                {
                    TargetMapName = targetMapName,
                    TargetVpk = targetVpk,
                    LoadError = $"Could not load skybox world resource {skyboxWorldPath}.",
                };
                return;
            }

            var skyCamera = FindBlenderEntity(skyboxWorld, fileLoader, "sky_camera");
            if (skyCamera == null)
            {
                manifest.Skybox = new BlenderSkyboxInfo
                {
                    TargetMapName = targetMapName,
                    TargetVpk = targetVpk,
                    WorldResource = skyboxWorldPath,
                    LoadError = "Skybox map did not contain a sky_camera entity.",
                };
                return;
            }

            EntityTransformHelper.DecomposeTransformationMatrix(skyboxReference, out _, out var skyboxReferenceRotation, out var skyboxReferencePosition);
            var skyboxReferenceTransform = skyboxReferenceRotation * Matrix4x4.CreateTranslation(skyboxReferencePosition);

            var worldOffset = EntityTransformHelper.CalculateTransformationMatrix(skyCamera).Translation;
            var worldScale = skyCamera.GetFloatProperty("scale", 1.0f);
            var skyboxTransform = Matrix4x4.CreateTranslation(-worldOffset)
                * Matrix4x4.CreateScale(worldScale)
                * skyboxReferenceTransform;

            var objectCountBefore = manifest.Objects.Count;
            ExportBlenderWorldGeometry(fileLoader, skyboxWorldPath, outputPath, manifest, isSkybox: true);

            manifest.Skybox = new BlenderSkyboxInfo
            {
                TargetMapName = targetMapName,
                TargetVpk = targetVpk,
                WorldResource = skyboxWorldPath,
                Transform = ToFloatArray(skyboxTransform),
                ReferenceOrigin = [skyboxReferencePosition.X, skyboxReferencePosition.Y, skyboxReferencePosition.Z],
                SkyCameraOrigin = [worldOffset.X, worldOffset.Y, worldOffset.Z],
                SkyCameraScale = worldScale,
                ObjectCount = manifest.Objects.Count - objectCountBefore,
            };
        }
        catch (Exception e)
        {
            manifest.Skybox = new BlenderSkyboxInfo
            {
                TargetMapName = targetMapName,
                TargetVpk = targetVpk,
                LoadError = e.Message,
            };
        }
        finally
        {
            if (addedSkyboxPackage != null)
            {
                fileLoader.RemovePackageFromSearch(addedSkyboxPackage);
                addedSkyboxPackage.Dispose();
            }
        }
    }

    private static EntityLump.Entity? FindBlenderEntity(VWorld world, GameFileLoader fileLoader, string classname)
    {
        foreach (var lumpName in world.GetEntityLumpNames())
        {
            if (string.IsNullOrWhiteSpace(lumpName))
            {
                continue;
            }

            using var entityLumpResource = fileLoader.LoadFileCompiled(lumpName);
            if (entityLumpResource?.DataBlock is not EntityLump entityLump)
            {
                continue;
            }

            var entity = FindBlenderEntity(entityLump, fileLoader, classname);
            if (entity != null)
            {
                return entity;
            }
        }

        return null;
    }

    private static EntityLump.Entity? FindBlenderEntity(EntityLump entityLump, GameFileLoader fileLoader, string classname)
    {
        foreach (var entity in entityLump.GetEntities())
        {
            if (string.Equals(entity.GetStringProperty("classname"), classname, StringComparison.OrdinalIgnoreCase))
            {
                return entity;
            }
        }

        foreach (var childEntityName in entityLump.GetChildEntityNames())
        {
            using var childResource = fileLoader.LoadFileCompiled(childEntityName);
            if (childResource?.DataBlock is not EntityLump childLump)
            {
                continue;
            }

            var entity = FindBlenderEntity(childLump, fileLoader, classname);
            if (entity != null)
            {
                return entity;
            }
        }

        return null;
    }

    private static void ExportBlenderMaterials(GameFileLoader fileLoader, string outputPath, BlenderMapManifest manifest)
    {
        var texturesDirectory = Path.Combine(outputPath, "textures");
        var exportedTexturePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var materialPaths = manifest.Meshes
            .SelectMany(static mesh => mesh.Materials)
            .Where(static material => !string.IsNullOrWhiteSpace(material))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (var materialPath in materialPaths)
        {
            using var materialResource = fileLoader.LoadFileCompiled(materialPath);
            if (materialResource?.DataBlock is not VMaterial material)
            {
                manifest.Materials.Add(new BlenderMaterial
                {
                    Path = materialPath,
                    Name = Path.GetFileNameWithoutExtension(materialPath),
                    LoadError = "Material resource could not be loaded.",
                });
                continue;
            }

            var exportedTextures = new Dictionary<string, string>(StringComparer.Ordinal);
            var textureErrors = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (textureKey, texturePath) in material.TextureParams.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(texturePath))
                {
                    continue;
                }

                if (exportedTexturePaths.TryGetValue(texturePath, out var existingTexture))
                {
                    exportedTextures[textureKey] = existingTexture;
                    continue;
                }

                try
                {
                    var exportedTexture = ExportBlenderTexture(fileLoader, texturesDirectory, texturePath);
                    if (exportedTexture == null)
                    {
                        textureErrors[textureKey] = $"Texture resource could not be loaded: {texturePath}";
                        continue;
                    }

                    exportedTexturePaths[texturePath] = exportedTexture;
                    exportedTextures[textureKey] = exportedTexture;
                    manifest.ExportedTextureCount++;
                }
                catch (Exception e)
                {
                    textureErrors[textureKey] = e.Message;
                }
            }

            manifest.Materials.Add(new BlenderMaterial
            {
                Path = materialPath,
                Name = material.Name,
                ShaderName = material.ShaderName,
                IntParams = new Dictionary<string, long>(material.IntParams, StringComparer.Ordinal),
                FloatParams = new Dictionary<string, float>(material.FloatParams, StringComparer.Ordinal),
                VectorParams = material.VectorParams.ToDictionary(
                    static pair => pair.Key,
                    static pair => new[] { pair.Value.X, pair.Value.Y, pair.Value.Z, pair.Value.W },
                    StringComparer.Ordinal),
                TextureParams = new Dictionary<string, string>(material.TextureParams, StringComparer.Ordinal),
                ExportedTextures = exportedTextures,
                TextureErrors = textureErrors.Count > 0 ? textureErrors : null,
            });
        }

        if (manifest.Materials.Count > 0)
        {
            manifest.Status = "geometry_materials";
            manifest.Notes =
            [
                "Exports static world-node scene objects as mesh payloads.",
                "Aggregate scene objects are exported as draw-call fragment meshes.",
                "Materials export raw VMAT params and decoded texture PNGs for baseline Blender node creation.",
                "Clutter, entity-lump, and shader-specific material graphs are not fully exported yet."
            ];
        }
    }

    private static void ExportBlenderWorldLighting(GameFileLoader fileLoader, VWorld world, string outputPath, BlenderMapManifest manifest)
    {
        var worldLightingInfo = world.GetWorldLightingInfo();
        if (worldLightingInfo == null)
        {
            return;
        }

        var lightmapVersion = worldLightingInfo.GetInt32Property("m_nLightmapVersionNumber");
        var lightmapGameVersion = lightmapVersion == 8
            ? worldLightingInfo.GetInt32Property("m_nLightmapGameVersionNumber")
            : 0;
        var lightmapUvScale = lightmapVersion == 8
            ? worldLightingInfo.GetSubCollection("m_vLightmapUvScale").ToVector2()
            : Vector2.One;

        var texturesDirectory = Path.Combine(outputPath, "textures");
        var exportedLightmaps = new Dictionary<string, string>(StringComparer.Ordinal);
        var lightmaps = worldLightingInfo.GetArray<string>("m_lightMaps") ?? [];
        foreach (var lightmap in lightmaps)
        {
            var uniformName = LightmapUniformName(lightmap);
            if (uniformName == null || exportedLightmaps.ContainsKey(uniformName))
            {
                continue;
            }

            try
            {
                var exportedTexture = ExportBlenderTexture(fileLoader, texturesDirectory, lightmap, forceLdr: true);
                if (exportedTexture != null)
                {
                    exportedLightmaps[uniformName] = exportedTexture;
                    manifest.ExportedTextureCount++;
                }
            }
            catch (Exception e)
            {
                manifest.LightmapExportErrors[uniformName] = e.Message;
            }
        }

        manifest.Lighting = new BlenderLightingInfo
        {
            LightmapVersion = lightmapVersion,
            LightmapGameVersion = lightmapGameVersion,
            LightmapUvScale = [lightmapUvScale.X, lightmapUvScale.Y],
            Lightmaps = exportedLightmaps,
        };
    }

    private static string? LightmapUniformName(string lightmapPath)
    {
        var name = Path.GetFileNameWithoutExtension(lightmapPath);
        return name switch
        {
            "irradiance" => "g_tIrradiance",
            "directional_irradiance_sh2_dc" => "g_tIrradiance",
            "directional_irradiance" => "g_tDirectionalIrradiance",
            "directional_irradiance_sh2_r" => "g_tDirectionalIrradianceR",
            "directional_irradiance_sh2_g" => "g_tDirectionalIrradianceG",
            "directional_irradiance_sh2_b" => "g_tDirectionalIrradianceB",
            "direct_light_shadows" => "g_tDirectLightShadows",
            "direct_light_indices" => "g_tDirectLightIndices",
            "direct_light_strengths" => "g_tDirectLightStrengths",
            "debug_chart_color" => "g_tIrradianceDebugChart",
            _ => null,
        };
    }

    private static string? ExportBlenderTexture(GameFileLoader fileLoader, string texturesDirectory, string texturePath, bool forceLdr = false)
    {
        using var textureResource = fileLoader.LoadFileCompiled(texturePath);
        if (textureResource?.DataBlock is not VTexture texture)
        {
            return null;
        }

        var textureHash = MurmurHash2.Hash(texturePath, StringToken.MURMUR2SEED);
        var fileName = $"{Path.GetFileNameWithoutExtension(texturePath)}_{textureHash:x8}.png";
        var relativePath = Path.Combine("textures", fileName);
        var outputPath = Path.Combine(texturesDirectory, fileName);

        if (File.Exists(outputPath))
        {
            return relativePath;
        }

        using var bitmap = texture.GenerateBitmap(decodeFlags: forceLdr ? TextureCodec.ForceLDR : TextureCodec.Auto);
        File.WriteAllBytes(outputPath, TextureExtract.ToPngImage(bitmap));
        return relativePath;
    }

    private static void ExportBlenderSceneObject(
        GameFileLoader fileLoader,
        string meshesDirectory,
        BlenderMapManifest manifest,
        Dictionary<string, int> meshCache,
        KVObject sceneObject,
        Matrix4x4 objectTransform,
        bool isSkybox)
    {
        var renderableModel = sceneObject.GetStringProperty("m_renderableModel");
        if (string.IsNullOrEmpty(renderableModel))
        {
            return;
        }

        using var modelResource = fileLoader.LoadFileCompiled(renderableModel);
        if (modelResource?.DataBlock is not VModel model)
        {
            return;
        }

        var objectMatrix = sceneObject.GetArray("m_vTransform").ToMatrix4x4() * objectTransform;
        var tintColor = sceneObject.GetSubCollection("m_vTintColor").ToVector4();
        if (tintColor == Vector4.Zero)
        {
            tintColor = Vector4.One;
        }

        var modelName = Path.GetFileNameWithoutExtension(renderableModel);
        foreach (var modelMesh in LoadBlenderModelMeshes(fileLoader, model, modelName))
        {
            var meshKey = $"{renderableModel}|{modelMesh.Name}";
            if (!meshCache.TryGetValue(meshKey, out var meshIndex))
            {
                meshIndex = manifest.Meshes.Count;
                var meshFileName = $"mesh_{meshIndex:D6}.json";
                var mesh = ExportBlenderMesh(fileLoader, meshesDirectory, meshFileName, modelMesh.Name, modelMesh.Mesh);
                if (mesh == null)
                {
                    continue;
                }

                meshCache.Add(meshKey, meshIndex);
                manifest.Meshes.Add(mesh);
            }

            manifest.Objects.Add(new BlenderObject
            {
                Name = modelMesh.Name,
                Mesh = meshIndex,
                Transform = ToFloatArray(objectMatrix),
                Tint = [tintColor.X, tintColor.Y, tintColor.Z, tintColor.W],
                SourceModel = renderableModel,
                IsSkybox = isSkybox,
            });
        }
    }

    private static bool ExportBlenderAggregateSceneObject(
        GameFileLoader fileLoader,
        string meshesDirectory,
        BlenderMapManifest manifest,
        Dictionary<string, int> meshCache,
        KVObject aggregateSceneObject,
        Matrix4x4 objectTransform,
        bool isSkybox)
    {
        var renderableModel = aggregateSceneObject.GetStringProperty("m_renderableModel");
        if (string.IsNullOrEmpty(renderableModel))
        {
            return false;
        }

        using var modelResource = fileLoader.LoadFileCompiled(renderableModel);
        if (modelResource?.DataBlock is not VModel model)
        {
            return false;
        }

        var aggregateMesh = LoadBlenderAggregateMesh(fileLoader, model);
        if (aggregateMesh == null)
        {
            return false;
        }

        var drawCalls = CollectBlenderDrawCalls(aggregateMesh).ToArray();
        var aggregateMeshes = aggregateSceneObject.GetArray("m_aggregateMeshes");
        if (aggregateMeshes.Count > 0 && !aggregateMeshes[0].ContainsKey("m_nDrawCallIndex"))
        {
            return false;
        }

        IReadOnlyList<KVObject> fragmentTransforms = aggregateSceneObject.ContainsKey("m_fragmentTransforms")
            ? aggregateSceneObject.GetArray("m_fragmentTransforms")
            : Array.Empty<KVObject>();
        var transformIndex = 0;
        var modelName = Path.GetFileNameWithoutExtension(renderableModel);
        var fragmentIndex = 0;

        foreach (var fragment in aggregateMeshes)
        {
            var lodGroupMask = fragment.GetUInt32Property("m_nLODGroupMask");
            if (lodGroupMask > 1)
            {
                continue;
            }

            var drawCallIndex = fragment.GetInt32Property("m_nDrawCallIndex");
            if (drawCallIndex < 0 || drawCallIndex >= drawCalls.Length)
            {
                continue;
            }

            var meshKey = $"{renderableModel}|aggregate|{drawCallIndex}";
            if (!meshCache.TryGetValue(meshKey, out var meshIndex))
            {
                meshIndex = manifest.Meshes.Count;
                var meshFileName = $"mesh_{meshIndex:D6}.json";
                var mesh = ExportBlenderMesh(fileLoader, meshesDirectory, meshFileName, $"{modelName}.fragment{drawCallIndex}", aggregateMesh, [drawCalls[drawCallIndex]], preferMeshlets: true);
                if (mesh == null)
                {
                    continue;
                }

                meshCache.Add(meshKey, meshIndex);
                manifest.Meshes.Add(mesh);
            }

            var transform = Matrix4x4.Identity;
            if (fragment.GetBooleanProperty("m_bHasTransform") == true && transformIndex < fragmentTransforms.Count)
            {
                transform = fragmentTransforms[transformIndex++].ToMatrix4x4();
            }
            transform *= objectTransform;

            var tintColor = Vector4.One;
            if (fragment.ContainsKey("m_vTintColor"))
            {
                var fragmentTintColor = fragment.GetSubCollection("m_vTintColor").ToVector3();
                tintColor = new Vector4(fragmentTintColor / 255f, 1.0f);
            }

            manifest.Objects.Add(new BlenderObject
            {
                Name = $"{modelName}_fragment{fragmentIndex++}",
                Mesh = meshIndex,
                Transform = ToFloatArray(transform),
                Tint = [tintColor.X, tintColor.Y, tintColor.Z, tintColor.W],
                SourceModel = renderableModel,
                IsSkybox = isSkybox,
            });
        }

        return true;
    }

    private static IEnumerable<(VMesh Mesh, string Name)> LoadBlenderModelMeshes(GameFileLoader fileLoader, VModel model, string name)
    {
        foreach (var mesh in model.GetEmbeddedMeshesAndLoD().Where(static mesh => (mesh.LoDMask & 1) != 0))
        {
            yield return (mesh.Mesh, string.Concat(name, ".", mesh.Name));
        }

        foreach (var mesh in model.GetReferenceMeshNamesAndLoD().Where(static mesh => (mesh.LoDMask & 1) != 0))
        {
            var meshResource = fileLoader.LoadFileCompiled(mesh.MeshName);
            if (meshResource?.DataBlock is VMesh referencedMesh)
            {
                yield return (referencedMesh, Path.GetFileNameWithoutExtension(mesh.MeshName));
            }
        }
    }

    private static VMesh? LoadBlenderAggregateMesh(GameFileLoader fileLoader, VModel model)
    {
        var embeddedMeshes = model.GetEmbeddedMeshesAndLoD().Where(static mesh => (mesh.LoDMask & 1) != 0).ToArray();
        if (embeddedMeshes.Length > 0)
        {
            return embeddedMeshes[0].Mesh;
        }

        var referenceMeshes = model.GetReferenceMeshNamesAndLoD().Where(static mesh => (mesh.LoDMask & 1) != 0).ToArray();
        if (referenceMeshes.Length == 0)
        {
            return null;
        }

        var meshResource = fileLoader.LoadFileCompiled(referenceMeshes[0].MeshName);
        return meshResource?.DataBlock as VMesh;
    }

    private static IEnumerable<KVObject> CollectBlenderDrawCalls(VMesh mesh)
    {
        foreach (var sceneObject in mesh.Data.GetArray("m_sceneObjects"))
        {
            foreach (var drawCall in sceneObject.GetArray("m_drawCalls"))
            {
                yield return drawCall;
            }
        }
    }

    private static IEnumerable<KVObject> CollectBlenderMeshlets(VMesh mesh)
    {
        foreach (var sceneObject in mesh.Data.GetArray("m_sceneObjects"))
        {
            var meshlets = sceneObject.GetArray("m_meshlets");
            if (meshlets == null)
            {
                continue;
            }

            foreach (var meshlet in meshlets)
            {
                yield return meshlet;
            }
        }
    }

    private static BlenderMesh? ExportBlenderMesh(GameFileLoader fileLoader, string meshesDirectory, string meshFileName, string meshName, VMesh mesh)
        => ExportBlenderMesh(fileLoader, meshesDirectory, meshFileName, meshName, mesh, CollectBlenderDrawCalls(mesh));

    private static BlenderMesh? ExportBlenderMesh(
        GameFileLoader fileLoader,
        string meshesDirectory,
        string meshFileName,
        string meshName,
        VMesh mesh,
        IEnumerable<KVObject> drawCalls,
        bool preferMeshlets = false)
    {
        if (mesh.Data.GetArray("m_sceneObjects").Count == 0)
        {
            return null;
        }

        var vbib = mesh.VBIB;
        var drawCallArray = drawCalls.ToArray();
        var attributes = CollectBlenderMeshAttributes(fileLoader, mesh, vbib, drawCallArray);
        if (attributes.Positions == null)
        {
            return null;
        }

        var indices = new List<int>();
        var primitives = new List<BlenderPrimitive>();
        var materials = new List<string>();
        KVObject[] meshlets = preferMeshlets ? CollectBlenderMeshlets(mesh).ToArray() : [];

        foreach (var drawCall in drawCallArray)
        {
            var primitiveType = drawCall.GetEnumValue<RenderPrimitiveType>("m_nPrimitiveType");
            if (primitiveType != RenderPrimitiveType.RENDER_PRIM_TRIANGLES)
            {
                continue;
            }

            var indexBufferInfo = drawCall.GetSubCollection("m_indexBuffer");
            var indexBufferIndex = indexBufferInfo.GetInt32Property("m_hBuffer");
            var indexBuffer = vbib.IndexBuffers[indexBufferIndex];
            var baseVertex = drawCall.GetInt32Property("m_nBaseVertex");
            var startIndex = drawCall.GetInt32Property("m_nStartIndex");
            var indexCount = drawCall.GetInt32Property("m_nIndexCount");
            var drawIndices = ReadBlenderDrawIndices(indexBuffer, meshlets, drawCall, startIndex, indexCount, baseVertex);
            if (drawIndices.Length == 0)
            {
                continue;
            }

            var material = drawCall.GetStringProperty("m_material") ?? drawCall.GetStringProperty("m_pMaterial") ?? string.Empty;
            var materialIndex = materials.IndexOf(material);
            if (materialIndex < 0)
            {
                materialIndex = materials.Count;
                materials.Add(material);
            }

            var firstIndex = indices.Count;
            indices.AddRange(drawIndices);
            primitives.Add(new BlenderPrimitive
            {
                FirstIndex = firstIndex,
                IndexCount = drawIndices.Length,
                Material = materialIndex,
            });
        }

        if (indices.Count == 0)
        {
            return null;
        }

        var meshPath = Path.Combine(meshesDirectory, meshFileName);
        var fileStem = Path.GetFileNameWithoutExtension(meshFileName);
        var positionsPath = Path.Combine("meshes", fileStem + ".positions.bin");
        var indicesPath = Path.Combine("meshes", fileStem + ".indices.bin");
        var normalsPath = attributes.Normals != null ? Path.Combine("meshes", fileStem + ".normals.bin") : null;
        var uv0Path = attributes.Uv0 != null ? Path.Combine("meshes", fileStem + ".uv0.bin") : null;
        var lightmapUvPath = attributes.LightmapUv != null ? Path.Combine("meshes", fileStem + ".lightmap_uv.bin") : null;
        var blendColorPath = attributes.BlendColor != null ? Path.Combine("meshes", fileStem + ".blend_color.bin") : null;
        var perVertexLightingPath = attributes.PerVertexLighting != null ? Path.Combine("meshes", fileStem + ".per_vertex_lighting.bin") : null;

        WriteVector3Array(Path.Combine(meshesDirectory, fileStem + ".positions.bin"), attributes.Positions);
        WriteIndexArray(Path.Combine(meshesDirectory, fileStem + ".indices.bin"), indices);

        if (attributes.Normals != null)
        {
            WriteVector3Array(Path.Combine(meshesDirectory, fileStem + ".normals.bin"), attributes.Normals);
        }

        if (attributes.Uv0 != null)
        {
            WriteVector2Array(Path.Combine(meshesDirectory, fileStem + ".uv0.bin"), attributes.Uv0);
        }

        if (attributes.LightmapUv != null)
        {
            WriteVector2Array(Path.Combine(meshesDirectory, fileStem + ".lightmap_uv.bin"), attributes.LightmapUv);
        }

        if (attributes.BlendColor != null)
        {
            WriteVector4Array(Path.Combine(meshesDirectory, fileStem + ".blend_color.bin"), attributes.BlendColor);
        }

        if (attributes.PerVertexLighting != null)
        {
            WriteVector4Array(Path.Combine(meshesDirectory, fileStem + ".per_vertex_lighting.bin"), attributes.PerVertexLighting);
        }

        var blenderMesh = new BlenderMesh
        {
            Name = meshName,
            Path = Path.Combine("meshes", meshFileName),
            VertexCount = attributes.Positions.Length,
            IndexCount = indices.Count,
            Positions = positionsPath,
            Normals = normalsPath,
            Uv0 = uv0Path,
            LightmapUv = lightmapUvPath,
            BlendColor = blendColorPath,
            PerVertexLighting = perVertexLightingPath,
            Indices = indicesPath,
            Materials = [.. materials],
            Primitives = [.. primitives],
        };

        File.WriteAllText(meshPath, JsonSerializer.Serialize(blenderMesh, BlenderPackageJsonOptions));
        return blenderMesh;
    }

    private static int[] ReadBlenderDrawIndices(
        ValveResourceFormat.Blocks.VBIB.OnDiskBufferData indexBuffer,
        KVObject[] meshlets,
        KVObject drawCall,
        int startIndex,
        int indexCount,
        int baseVertex)
    {
        if (meshlets.Length > 0 && drawCall.ContainsKey("m_nFirstMeshlet") && drawCall.ContainsKey("m_nNumMeshlets"))
        {
            var firstMeshlet = drawCall.GetInt32Property("m_nFirstMeshlet");
            var meshletCount = drawCall.GetInt32Property("m_nNumMeshlets");
            var indices = new List<int>();
            var fallbackTriangleCount = meshletCount > 0 ? (indexCount / 3) / meshletCount : 0;

            for (var i = firstMeshlet; i < firstMeshlet + meshletCount && i < meshlets.Length; i++)
            {
                var meshlet = meshlets[i];
                var triangleOffset = meshlet.GetInt32Property("m_nTriangleOffset");
                var triangleCount = (int)meshlet.GetUInt32Property("m_nTriangleCount");

                if (triangleOffset == 0 && triangleCount == 0 && fallbackTriangleCount > 0)
                {
                    triangleOffset = i * fallbackTriangleCount;
                    triangleCount = fallbackTriangleCount;
                }

                var meshletIndexCount = triangleCount * 3;
                if (meshletIndexCount <= 0)
                {
                    continue;
                }

                indices.AddRange(GltfModelExporter.ReadIndices(indexBuffer, triangleOffset * 3, meshletIndexCount, baseVertex));
            }

            return [.. indices];
        }

        indexCount -= indexCount % 3;
        return indexCount > 0
            ? GltfModelExporter.ReadIndices(indexBuffer, startIndex, indexCount, baseVertex)
            : [];
    }

    private static BlenderMeshAttributes CollectBlenderMeshAttributes(
        GameFileLoader fileLoader,
        VMesh mesh,
        ValveResourceFormat.Blocks.VBIB vbib,
        IReadOnlyList<KVObject> drawCalls)
    {
        Vector3[]? positions = null;
        Vector3[]? normals = null;
        Vector2[]? uv0 = null;
        Vector2[]? lightmapUv = null;
        Vector4[]? blendColor = null;
        Vector4[]? perVertexLighting = null;
        var materialInputSignatures = new Dictionary<string, VMaterial.VsInputSignature>(StringComparer.Ordinal);

        foreach (var drawCall in drawCalls)
        {
            var materialInputSignature = GetMaterialInputSignature(fileLoader, drawCall, materialInputSignatures);
            foreach (var vertexBufferInfo in drawCall.GetArray("m_vertexBuffers"))
            {
                var vertexBufferIndex = vertexBufferInfo.GetInt32Property("m_hBuffer");
                var vertexBuffer = vbib.VertexBuffers[vertexBufferIndex];

                foreach (var attribute in vertexBuffer.InputLayoutFields.OrderBy(static field => field.SemanticIndex).ThenBy(static field => field.Offset))
                {
                    var attributeFormat = ValveResourceFormat.Blocks.VBIB.GetFormatInfo(attribute);
                    var shaderSemantic = ResolveShaderSemantic(attribute, materialInputSignature);
                    if (positions == null && attribute.SemanticName == "POSITION" && attributeFormat.ElementCount == 3)
                    {
                        positions = ValveResourceFormat.Blocks.VBIB.GetVector3AttributeArray(vertexBuffer, attribute);
                    }
                    else if (normals == null && attribute.SemanticName == "NORMAL")
                    {
                        normals = ValveResourceFormat.Blocks.VBIB.GetNormalTangentArray(vertexBuffer, attribute).Normals;
                    }
                    else if (lightmapUv == null && IsLightmapSemantic(shaderSemantic) && attributeFormat.ElementCount == 2)
                    {
                        lightmapUv = ValveResourceFormat.Blocks.VBIB.GetVector2AttributeArray(vertexBuffer, attribute);
                    }
                    else if (uv0 == null && attribute.SemanticName == "TEXCOORD" && !IsLightmapSemantic(shaderSemantic) && attributeFormat.ElementCount == 2)
                    {
                        uv0 = ValveResourceFormat.Blocks.VBIB.GetVector2AttributeArray(vertexBuffer, attribute);
                    }
                    else if (blendColor == null && attribute.SemanticName == "TEXCOORD" && shaderSemantic == "VertexPaintBlendParams" && attributeFormat.ElementCount == 4)
                    {
                        blendColor = ValveResourceFormat.Blocks.VBIB.GetVector4AttributeArray(vertexBuffer, attribute);
                    }
                    else if (perVertexLighting == null && IsPerVertexLightingSemantic(shaderSemantic) && attributeFormat.ElementCount == 4)
                    {
                        perVertexLighting = ValveResourceFormat.Blocks.VBIB.GetVector4AttributeArray(vertexBuffer, attribute);
                    }
                }
            }
        }

        return new BlenderMeshAttributes(positions, normals, uv0, lightmapUv, blendColor, perVertexLighting);
    }

    private static VMaterial.VsInputSignature GetMaterialInputSignature(
        GameFileLoader fileLoader,
        KVObject drawCall,
        Dictionary<string, VMaterial.VsInputSignature> materialInputSignatures)
    {
        var materialPath = drawCall.GetStringProperty("m_material") ?? drawCall.GetStringProperty("m_pMaterial") ?? string.Empty;
        if (string.IsNullOrEmpty(materialPath))
        {
            return VMaterial.VsInputSignature.Empty;
        }

        if (materialInputSignatures.TryGetValue(materialPath, out var inputSignature))
        {
            return inputSignature;
        }

        using var materialResource = fileLoader.LoadFileCompiled(materialPath);
        inputSignature = materialResource?.DataBlock is VMaterial material
            ? material.InputSignature
            : VMaterial.VsInputSignature.Empty;
        materialInputSignatures[materialPath] = inputSignature;
        return inputSignature;
    }

    private static string ResolveShaderSemantic(ValveResourceFormat.Blocks.VBIB.RenderInputLayoutField attribute, VMaterial.VsInputSignature inputSignature)
    {
        if (!string.IsNullOrEmpty(attribute.ShaderSemantic))
        {
            return attribute.ShaderSemantic;
        }

        if (inputSignature.Elements.Length == 0)
        {
            return string.Empty;
        }

        var inputElement = VMaterial.FindD3DInputSignatureElement(inputSignature, attribute.SemanticName, attribute.SemanticIndex);
        return !string.IsNullOrEmpty(inputElement.Semantic) ? inputElement.Semantic : inputElement.Name;
    }

    private static bool IsLightmapSemantic(string? actual)
        => actual is "LightmapUV" or "vLightmapUV" or "vLightmapUVW";

    private static bool IsPerVertexLightingSemantic(string? actual)
        => actual is "PerVertexLighting" or "vPerVertexLighting";

    private static void WriteVector2Array(string path, Vector2[] values)
    {
        using var writer = new BinaryWriter(File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None));
        foreach (var value in values)
        {
            writer.Write(value.X);
            writer.Write(value.Y);
        }
    }

    private static void WriteVector3Array(string path, Vector3[] values)
    {
        using var writer = new BinaryWriter(File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None));
        foreach (var value in values)
        {
            writer.Write(value.X);
            writer.Write(value.Y);
            writer.Write(value.Z);
        }
    }

    private static void WriteVector4Array(string path, Vector4[] values)
    {
        using var writer = new BinaryWriter(File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None));
        foreach (var value in values)
        {
            writer.Write(value.X);
            writer.Write(value.Y);
            writer.Write(value.Z);
            writer.Write(value.W);
        }
    }

    private static void WriteIndexArray(string path, List<int> values)
    {
        using var writer = new BinaryWriter(File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None));
        foreach (var value in values)
        {
            writer.Write(value);
        }
    }

    private static float[] ToFloatArray(Matrix4x4 matrix)
        =>
        [
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44,
        ];

    private sealed class BlenderMapManifest
    {
        public required int Version { get; init; }
        public required string Generator { get; init; }
        public required string MapVpk { get; init; }
        public required string MapName { get; init; }
        public string? MainMapResource { get; init; }
        public string? MainMapResourceType { get; set; }
        public string? MainMapLoadError { get; set; }
        public string? GeometryExportError { get; set; }
        public required Dictionary<string, int> ResourceCounts { get; init; }
        public required string[] MapResources { get; init; }
        public required string Status { get; set; }
        public required string[] Notes { get; set; }
        public List<BlenderMesh> Meshes { get; } = [];
        public List<BlenderObject> Objects { get; } = [];
        public List<BlenderMaterial> Materials { get; } = [];
        public BlenderLightingInfo? Lighting { get; set; }
        public BlenderSkyboxInfo? Skybox { get; set; }
        public Dictionary<string, string> LightmapExportErrors { get; } = new(StringComparer.Ordinal);
        public int ExportedTextureCount { get; set; }
        public int UnsupportedAggregateSceneObjects { get; set; }
        public int UnsupportedClutterSceneObjects { get; set; }
    }

    private sealed record BlenderMeshAttributes(
        Vector3[]? Positions,
        Vector3[]? Normals,
        Vector2[]? Uv0,
        Vector2[]? LightmapUv,
        Vector4[]? BlendColor,
        Vector4[]? PerVertexLighting);

    private sealed class BlenderLightingInfo
    {
        public required int LightmapVersion { get; init; }
        public required int LightmapGameVersion { get; init; }
        public required float[] LightmapUvScale { get; init; }
        public required Dictionary<string, string> Lightmaps { get; init; }
    }

    private sealed class BlenderSkyboxInfo
    {
        public required string TargetMapName { get; init; }
        public required string TargetVpk { get; init; }
        public string? WorldResource { get; init; }
        public float[]? Transform { get; init; }
        public float[]? ReferenceOrigin { get; init; }
        public float[]? SkyCameraOrigin { get; init; }
        public float SkyCameraScale { get; init; }
        public int ObjectCount { get; init; }
        public string? LoadError { get; init; }
    }

    private sealed class BlenderMesh
    {
        public required string Name { get; init; }
        public required string Path { get; init; }
        public required int VertexCount { get; init; }
        public required int IndexCount { get; init; }
        public required string Positions { get; init; }
        public string? Normals { get; init; }
        public string? Uv0 { get; init; }
        public string? LightmapUv { get; init; }
        public string? BlendColor { get; init; }
        public string? PerVertexLighting { get; init; }
        public required string Indices { get; init; }
        public required string[] Materials { get; init; }
        public required BlenderPrimitive[] Primitives { get; init; }
    }

    private sealed class BlenderPrimitive
    {
        public required int FirstIndex { get; init; }
        public required int IndexCount { get; init; }
        public required int Material { get; init; }
    }

    private sealed class BlenderObject
    {
        public required string Name { get; init; }
        public required int Mesh { get; init; }
        public required float[] Transform { get; init; }
        public required float[] Tint { get; init; }
        public required string SourceModel { get; init; }
        public bool IsSkybox { get; init; }
    }

    private sealed class BlenderMaterial
    {
        public required string Path { get; init; }
        public required string Name { get; init; }
        public string? ShaderName { get; init; }
        public string? LoadError { get; init; }
        public Dictionary<string, long>? IntParams { get; init; }
        public Dictionary<string, float>? FloatParams { get; init; }
        public Dictionary<string, float[]>? VectorParams { get; init; }
        public Dictionary<string, string>? TextureParams { get; init; }
        public Dictionary<string, string>? ExportedTextures { get; init; }
        public Dictionary<string, string>? TextureErrors { get; init; }
    }
}
