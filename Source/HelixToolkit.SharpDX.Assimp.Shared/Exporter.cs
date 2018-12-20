﻿/*
The MIT License (MIT)
Copyright (c) 2018 Helix Toolkit contributors
*/
using Assimp;
using Assimp.Configs;
using SharpDX;
using SharpDX.Direct3D11;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
#if !NETFX_CORE
namespace HelixToolkit.Wpf.SharpDX
#else
#if CORE
namespace HelixToolkit.SharpDX.Core
#else
namespace HelixToolkit.UWP
#endif
#endif
{
    using HelixToolkit.Logger;
    using Model;
    using System.Collections.ObjectModel;
    using HxAnimations = Animations;
    using HxScene = Model.Scene;

    namespace Assimp
    {
        public partial class Exporter : IDisposable
        {
            private const string ToUpperDictString = @"..\";

            static Exporter()
            {
                using (var temp = new AssimpContext())
                {
                    SupportedFormats = temp.GetSupportedExportFormats().ToArray();
                }
                var builder = new StringBuilder();
                foreach (var s in SupportedFormats)
                {
                    builder.Append($"{s.Description} (*.{s.FileExtension})|*.{ s.FileExtension }|");
                }
                SupportedFormatsString = builder.ToString(0, builder.Length - 1);
            }
            #region Properties
            /// <summary>
            ///     Gets the supported formats.
            /// </summary>
            /// <value>
            ///     The supported formats.
            /// </value>
            public static ExportFormatDescription[] SupportedFormats { get; }

            /// <summary>
            ///     Gets the supported formats string.
            /// </summary>
            /// <value>
            ///     The supported formats string.
            /// </value>
            public static string SupportedFormatsString { get; }

            private ExportConfiguration configuration = new ExportConfiguration();
            /// <summary>
            /// Gets or sets the configuration.
            /// </summary>
            /// <value>
            /// The configuration.
            /// </value>
            public ExportConfiguration Configuration
            {
                set
                {
                    configuration = value;
                    if (value == null)
                    {
                        configuration = new ExportConfiguration();
                    }
                }
                get
                {
                    return configuration;
                }
            }

            public ILogger Logger { get => configuration.Logger; }
            #endregion
            protected readonly Dictionary<Geometry3D, int> geometryCollection = new Dictionary<Geometry3D, int>();
            protected readonly Dictionary<MaterialCore, int> materialCollection = new Dictionary<MaterialCore, int>();
            protected readonly Dictionary<ulong, MeshInfo> meshInfos = new Dictionary<ulong, MeshInfo>();

            private int MaterialIndexForNoName = 0;
            private int MeshIndexForNoName = 0;

            public ErrorCode ExportToFile(string filePath, HxScene.SceneNode root, string formatId)
            {
                Clear();
                AssimpContext exporter = null;
                var useExtern = false;
                if (Configuration.ExternalContext != null)
                {
                    exporter = Configuration.ExternalContext;
                    useExtern = true;
                }
                else
                {
                    exporter = new AssimpContext();
                }
                if (!exporter.IsExportFormatSupported(Path.GetExtension(filePath)))
                {
                    return ErrorCode.Failed | ErrorCode.FileTypeNotSupported;
                }
                var scene = CreateScene(root);
                var postProcessing = configuration.PostProcessing;
                if (configuration.FlipWindingOrder)
                {
                    postProcessing |= PostProcessSteps.FlipWindingOrder;
                }
                try
                {
                    if(!exporter.ExportFile(scene, filePath, formatId, postProcessing))
                    {
                        Log(LogLevel.Error, $"Export failed. FilePath: {filePath}; Format: {formatId}");
                        return ErrorCode.Failed;
                    }
                    return ErrorCode.Succeed;
                }
                catch(Exception ex)
                {
                    Log(LogLevel.Error, ex.Message);
                }
                finally
                {
                    if (!useExtern)
                    {
                        exporter.Dispose();
                    }
                }
                return ErrorCode.Failed;
            }

            private Scene CreateScene(HxScene.SceneNode root)
            {
                CollectAllGeometriesAndMaterials(root);
                var scene = new Scene();
                //Adds material and meshes into the assimp scene
                foreach(var material in materialCollection.OrderBy(x=>x.Value))
                {
                    scene.Materials.Add(OnCreateAssimpMaterial(material.Key));
                }
                scene.RootNode = ConstructAssimpNode(root, null);
                scene.Meshes.AddRange(meshInfos.Select(x => x.Value.AssimpMesh));
                return scene;
            }

            private Node ConstructAssimpNode(HxScene.SceneNode current, Node parent)
            {
                var node = new Node(string.IsNullOrEmpty(current.Name) ? "Node" : current.Name, parent)
                {
                    Transform = current.ModelMatrix.ToAssimpMatrix()
                };
                if(current is HxScene.GroupNodeBase group)
                {
                    foreach(var s in group.Items)
                    {
                        if(s is HxScene.GeometryNode geo)
                        {
                            var info = OnCreateMeshInfo(geo);
                            if (info == null)
                            {
                                Log(LogLevel.Warning, $"Create Mesh info failed. Node Name: {geo.Name}");
                                continue;
                            }
                            if (!meshInfos.ContainsKey(info.MaterialMeshKey))
                            {
                                meshInfos.Add(info.MaterialMeshKey, info);
                            }
                            node.MeshIndices.Add(info.MeshIndex);
                        }
                        else if(s is HxScene.GroupNodeBase)
                        {
                            node.Children.Add(ConstructAssimpNode(s, node));
                        }
                    }
                }
                return node;
            }

            private void CollectAllGeometriesAndMaterials(HxScene.SceneNode root)
            {
                // Collect all geometries and materials
                foreach(var node in root.Traverse())
                {
                    if(GetMaterialFromNode(node, out var material) && !materialCollection.ContainsKey(material))
                    {
                        materialCollection.Add(material, materialCollection.Count);
                    }
                    if (GetGeometryFromNode(node, out var geometry) && !geometryCollection.ContainsKey(geometry))
                    {
                        geometryCollection.Add(geometry, geometryCollection.Count);
                    }
                }
                foreach (var node in root.Traverse())
                {
                    if (node is HxScene.GeometryNode geo)
                    {
                        var info = OnCreateMeshInfo(geo);
                        if (info == null)
                        {
                            Log(LogLevel.Warning, $"Create Mesh info failed. Node Name: {geo.Name}");
                            continue;
                        }
                        if (!meshInfos.ContainsKey(info.MaterialMeshKey))
                        {
                            meshInfos.Add(info.MaterialMeshKey, info);
                        }
                    }
                }
            }

            protected virtual void Clear()
            {
                geometryCollection.Clear();
                materialCollection.Clear();
                meshInfos.Clear();
                MaterialIndexForNoName = MeshIndexForNoName = 0;
            }

            /// <summary>
            /// Logs the specified level.
            /// </summary>
            /// <typeparam name="Type">The type of the ype.</typeparam>
            /// <param name="level">The level.</param>
            /// <param name="msg">The MSG.</param>
            /// <param name="caller">The caller.</param>
            /// <param name="sourceLineNumber">The source line number.</param>
            protected void Log<Type>(LogLevel level, Type msg, [CallerMemberName]string caller = "", [CallerLineNumber] int sourceLineNumber = 0)
            {
                Logger.Log(level, msg, nameof(EffectsManager), caller, sourceLineNumber);
            }
            #region Inner Classes

            /// <summary>
            /// </summary>
            protected sealed class HelixInternalScene
            {
                /// <summary>
                /// The animations
                /// </summary>
                public List<HxAnimations.Animation> Animations;

                /// <summary>
                /// The assimp scene
                /// </summary>
                public Scene AssimpScene;

                /// <summary>
                ///     The materials
                /// </summary>
                public Tuple<global::Assimp.Material, MaterialCore>[] Materials;

                /// <summary>
                ///     The meshes
                /// </summary>
                public MeshInfo[] Meshes;
            }

            #endregion

            #region IDisposable Support
            private bool disposedValue = false; // To detect redundant calls

            protected virtual void Dispose(bool disposing)
            {
                if (!disposedValue)
                {
                    if (disposing)
                    {
                        // TODO: dispose managed state (managed objects).
                        Clear();
                    }

                    // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                    // TODO: set large fields to null.

                    disposedValue = true;
                }
            }

            // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
            // ~Importer() {
            //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            //   Dispose(false);
            // }

            // This code added to correctly implement the disposable pattern.
            public void Dispose()
            {
                // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
                Dispose(true);
                // TODO: uncomment the following line if the finalizer is overridden above.
                // GC.SuppressFinalize(this);
            }
            #endregion
        }
    }
}
