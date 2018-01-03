﻿/*
Copyright (c) 2017, Lars Brubaker, John Lewin
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The views and conclusions contained in the software and documentation are those
of the authors and should not be interpreted as representing official policies,
either expressed or implied, of the FreeBSD Project.
*/
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MatterHackers.Agg;
using MatterHackers.Agg.Image;
using MatterHackers.Agg.OpenGlGui;
using MatterHackers.Agg.Platform;
using MatterHackers.Agg.Transform;
using MatterHackers.Agg.UI;
using MatterHackers.Agg.VertexSource;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.CustomWidgets;
using MatterHackers.MatterControl.DataStorage;
using MatterHackers.MatterControl.Library;
using MatterHackers.MatterControl.PrinterCommunication;
using MatterHackers.MatterControl.PrintLibrary;
using MatterHackers.MeshVisualizer;
using MatterHackers.PolygonMesh;
using MatterHackers.RayTracer;
using MatterHackers.RenderOpenGl;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.PartPreviewWindow
{
	public class View3DWidget : GuiWidget
	{
		private bool DoBooleanTest = false;
		private bool deferEditorTillMouseUp = false;

		public GuiWidget bottomActionPanel;

		public readonly int EditButtonHeight = 44;

		private ObservableCollection<GuiWidget> materialButtons = new ObservableCollection<GuiWidget>();
		private bool hasDrawn = false;

		private Color[] SelectionColors = new Color[] { new Color(131, 4, 66), new Color(227, 31, 61), new Color(255, 148, 1), new Color(247, 224, 23), new Color(143, 212, 1) };
		private Stopwatch timeSinceLastSpin = new Stopwatch();
		private Stopwatch timeSinceReported = new Stopwatch();
		private Matrix4X4 transformOnMouseDown = Matrix4X4.Identity;
		private EventHandler unregisterEvents;

		private ThemeConfig theme;

		public Vector3 BedCenter
		{
			get
			{
				return new Vector3(sceneContext.BedCenter);
			}
		}

		private WorldView World => sceneContext.World;

		public TrackballTumbleWidget TrackballTumbleWidget { get; }

		public InteractionLayer InteractionLayer { get; }

		private BedConfig sceneContext;

		private PrinterConfig printer;

		private PrinterTabPage printerTabPage;

		public View3DWidget(PrinterConfig printer, BedConfig sceneContext, AutoRotate autoRotate, ViewControls3D viewControls3D, ThemeConfig theme, PartTabPage printerTabBase, MeshViewerWidget.EditorType editorType = MeshViewerWidget.EditorType.Part)
		{
			var smallMarginButtonFactory = theme.SmallMarginButtonFactory;

			this.sceneContext = sceneContext;
			this.printerTabPage = printerTabBase as PrinterTabPage;
			this.Scene = sceneContext.Scene;
			this.printer = printer;

			this.TrackballTumbleWidget = new TrackballTumbleWidget(sceneContext.World)
			{
				TransformState = TrackBallController.MouseDownType.Rotation
			};
			this.TrackballTumbleWidget.AnchorAll();

			this.InteractionLayer = new InteractionLayer(this.World, this.Scene.UndoBuffer, this.Scene)
			{
				Name = "InteractionLayer",
			};
			this.InteractionLayer.AnchorAll();

			this.viewControls3D = viewControls3D;
			this.theme = theme;
			this.Name = "View3DWidget";
			this.BackgroundColor = theme.TabBodyBackground;

			autoRotating = allowAutoRotate;
			allowAutoRotate = (autoRotate == AutoRotate.Enabled);

			viewControls3D.TransformStateChanged += ViewControls3D_TransformStateChanged;

			var mainContainerTopToBottom = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.MaxFitOrStretch,
				VAnchor = VAnchor.MaxFitOrStretch
			};

			// MeshViewer
			meshViewerWidget = new MeshViewerWidget(sceneContext, this.InteractionLayer, editorType: editorType);
			meshViewerWidget.RenderBed = sceneContext.RendererOptions.RenderBed;
			meshViewerWidget.AnchorAll();
			this.InteractionLayer.AddChild(meshViewerWidget);

			// TumbleWidget
			this.InteractionLayer.AddChild(this.TrackballTumbleWidget);

			this.InteractionLayer.SetRenderTarget(this.meshViewerWidget);

			mainContainerTopToBottom.AddChild(this.InteractionLayer);

			Scene.SelectionChanged += Scene_SelectionChanged;

			// if the scene is invalidated invalidate the widget
			Scene.Invalidated += (s, e) => Invalidate();

			// add in the plater tools
			{
				bool isPrinterMode = meshViewerWidget.EditorMode == MeshViewerWidget.EditorType.Printer;
				var buttonSpacing = theme.ButtonSpacing;

				var buttonView = new FlowLayoutWidget();
				buttonView.AddChild(new ImageWidget(AggContext.StaticData.LoadIcon((isPrinterMode) ? "bed.png" : "cube.png", IconColor.Theme))
				{
					Margin = new BorderDouble(left: 10),
					VAnchor = VAnchor.Center
				});

				var buttonText = (isPrinterMode) ? "Bed".Localize() : "Part".Localize();
				buttonView.AddChild(new TextButton(buttonText, theme)
				{
					Padding = new BorderDouble(8, 4, 0, 4)
				});

				var selectionActionBar = new OverflowBar(theme, buttonView)
				{
					VAnchor = VAnchor.Center | VAnchor.Fit,
					HAnchor = HAnchor.Stretch,
				};
				selectionActionBar.OverflowMenu.Name = "Bed Options Menu";
				selectionActionBar.OverflowMenu.PopDirection = Direction.Up;
				selectionActionBar.OverflowMenu.DynamicPopupContent = () => theme.CreatePopupMenu(this.BedMenuActions(sceneContext));
				selectionActionBar.OverflowMenu.AlignToRightEdge = true;
				selectionActionBar.OverflowMenu.DrawArrow = true;

				bottomActionPanel = new DisableablePanel(selectionActionBar, enabled: true)
				{
					BackgroundColor = ActiveTheme.Instance.PrimaryBackgroundColor,
				};

				Button addButton = smallMarginButtonFactory.Generate("Insert".Localize(), AggContext.StaticData.LoadIcon("cube.png", 14, 14, IconColor.Theme));
				addButton.Margin = 0;
				addButton.Click += (sender, e) =>
				{
					UiThread.RunOnIdle(() =>
					{
						AggContext.FileDialogs.OpenFileDialog(
							new OpenFileDialogParams(ApplicationSettings.OpenDesignFileParams, multiSelect: true),
							(openParams) =>
							{
								this.LoadAndAddPartsToPlate(openParams.FileNames);
							});
					});
				};
				selectionActionBar.AddChild(addButton);

				selectionActionBar.AddChild(this.CreateActionSeparator());

				Button ungroupButton = smallMarginButtonFactory.Generate("Ungroup".Localize());
				ungroupButton.Name = "3D View Ungroup";
				ungroupButton.Margin = buttonSpacing;
				ungroupButton.Click += (sender, e) =>
				{
					this.Scene.UngroupSelection(this);
				};
				this.Scene.SelectionChanged += (s, e) =>
				{
					ungroupButton.Enabled = this.Scene.HasSelection;
				};
				selectionActionBar.AddChild(ungroupButton);

				Button groupButton = smallMarginButtonFactory.Generate("Group".Localize());
				groupButton.Name = "3D View Group";
				groupButton.Margin = buttonSpacing;
				groupButton.Click += (sender, e) =>
				{
					this.Scene.GroupSelection(this);
				};
				this.Scene.SelectionChanged += (s, e) =>
				{
					groupButton.Enabled = this.Scene.HasSelection
						&& this.Scene.SelectedItem is SelectionGroup
						&& this.Scene.SelectedItem.Children.Count > 1;
				};
				selectionActionBar.AddChild(groupButton);

				// this is closer to the old align button
				if (false)
				{
					var absoluteButton = smallMarginButtonFactory.Generate("Absolute".Localize());
					absoluteButton.Margin = buttonSpacing;
					absoluteButton.Click += (sender, e) =>
					{
						if (this.Scene.HasSelection)
						{
							this.Scene.SelectedItem.Matrix = Matrix4X4.Identity;
						}
					};
					selectionActionBar.AddChild(absoluteButton);
				}

				// put in the material options
				var alignButton = new PopupMenuButton("Align".Localize(), theme)
				{
					PopDirection = Direction.Up,
					PopupContent = this.AddAlignControls(),
					AlignToRightEdge = true,
					Margin = buttonSpacing
				};
				this.Scene.SelectionChanged += (s, e) =>
				{
					alignButton.Enabled = this.Scene.HasSelection
						&& this.Scene.SelectedItem is SelectionGroup
						&& this.Scene.SelectedItem.Children.Count > 1;
				};
				selectionActionBar.AddChild(alignButton);

				var layFlatButton = smallMarginButtonFactory.Generate("Lay Flat".Localize());
				layFlatButton.Margin = buttonSpacing;
				layFlatButton.Click += (sender, e) =>
				{
					if (this.Scene.HasSelection)
					{
						MakeLowestFaceFlat(this.Scene.SelectedItem);
					}
				};
				this.Scene.SelectionChanged += (s, e) =>
				{
					layFlatButton.Enabled = this.Scene.HasSelection;
				};
				selectionActionBar.AddChild(layFlatButton);

				selectionActionBar.AddChild(this.CreateActionSeparator());

				var duplicateButton = smallMarginButtonFactory.Generate("Duplicate".Localize());
				duplicateButton.Name = "3D View Copy";
				duplicateButton.Margin = buttonSpacing;
				duplicateButton.Click += (sender, e) =>
				{
					this.Scene.DuplicateSelection(this);
				};
				this.Scene.SelectionChanged += (s, e) =>
				{
					duplicateButton.Enabled = this.Scene.HasSelection;
				};
				selectionActionBar.AddChild(duplicateButton);

				var deleteButton = smallMarginButtonFactory.Generate("Remove".Localize());
				deleteButton.Name = "3D View Remove";
				deleteButton.Margin = buttonSpacing;
				deleteButton.Click += (sender, e) =>
				{
					this.Scene.DeleteSelection(this);
				};
				this.Scene.SelectionChanged += (s, e) =>
				{
					deleteButton.Enabled = this.Scene.HasSelection;
				};
				selectionActionBar.AddChild(deleteButton);

				var mirrorButton = new PopupMenuButton("Mirror".Localize(), theme)
				{
					Name = "Mirror Button",
					PopDirection = Direction.Up,
					PopupContent = new MirrorControls(Scene),
					AlignToRightEdge = true,
					Margin = buttonSpacing,
				};
				this.Scene.SelectionChanged += (s, e) =>
				{
					mirrorButton.Enabled = this.Scene.HasSelection;
				};
				selectionActionBar.AddChild(mirrorButton);

				var scaleButton = new PopupMenuButton("Scale".Localize(), theme)
				{
					Name = "Scale Button",
					PopDirection = Direction.Up,
					DynamicPopupContent = () => new ScaleControls(Scene, Color.Black),
					AlignToRightEdge = true,
					Margin = buttonSpacing,
				};
				this.Scene.SelectionChanged += (s, e) =>
				{
					scaleButton.Enabled = this.Scene.HasSelection;
				};
				selectionActionBar.AddChild(scaleButton);

				// put in the material options
				var materialsButton = new PopupMenuButton("Materials".Localize(), theme)
				{
					PopDirection = Direction.Up,
					PopupContent = this.AddMaterialControls(),
					AlignToRightEdge = true,
					Margin = buttonSpacing
				};
				this.Scene.SelectionChanged += (s, e) =>
				{
					materialsButton.Enabled = this.Scene.HasSelection;
				};
				selectionActionBar.AddChild(materialsButton);
			}

			mainContainerTopToBottom.AddChild(bottomActionPanel);

			this.AddChild(mainContainerTopToBottom);

			this.AnchorAll();

			this.TrackballTumbleWidget.TransformState = TrackBallController.MouseDownType.Rotation;

			selectedObjectPanel = new SelectedObjectPanel(this, this.Scene, theme, printer)
			{
				BackgroundColor = theme.InteractionLayerOverlayColor,
				VAnchor = VAnchor.Top | VAnchor.Fit,
				HAnchor = HAnchor.Left | HAnchor.Fit,
			};

			selectedObjectContainer = new ResizeContainer(selectedObjectPanel)
			{
				Width = 200,
				VAnchor = VAnchor.Fit | VAnchor.Top,
				HAnchor = HAnchor.Right,
				SpliterBarColor = theme.SplitterBackground,
				SplitterWidth = theme.SplitterWidth,
				Visible = false,
			};
			this.InteractionLayer.AddChild(selectedObjectContainer);
			selectedObjectContainer.AddChild(selectedObjectPanel);

			this.InteractionLayer.AddChild(new TumbleCubeControl(this.InteractionLayer)
			{
				Margin = new BorderDouble(50, 0, 0, 50),
				VAnchor = VAnchor.Top,
				HAnchor = HAnchor.Left,
			});

			UiThread.RunOnIdle(AutoSpin);

			var interactionVolumes = this.InteractionLayer.InteractionVolumes;
			interactionVolumes.Add(new MoveInZControl(this.InteractionLayer));
			interactionVolumes.Add(new SelectionShadow(this.InteractionLayer));
			interactionVolumes.Add(new SnappingIndicators(this.InteractionLayer, this.CurrentSelectInfo));

			var interactionVolumePlugins = PluginFinder.CreateInstancesOf<InteractionVolumePlugin>();
			foreach (InteractionVolumePlugin plugin in interactionVolumePlugins)
			{
				interactionVolumes.Add(plugin.CreateInteractionVolume(this.InteractionLayer));
			}

			if (DoBooleanTest)
			{
				BeforeDraw += CreateBooleanTestGeometry;
				AfterDraw += RemoveBooleanTestGeometry;
			}

			meshViewerWidget.AfterDraw += AfterDraw3DContent;

			sceneContext.LoadedGCodeChanged += SceneContext_LoadedGCodeChanged;

			this.SwitchStateToEditing();

			this.InteractionLayer.DrawGlOpaqueContent += Draw_GlOpaqueContent;

			this.sceneContext.SceneLoaded += SceneContext_SceneLoaded;

			this.viewControls3D.modelViewButton.Enabled = sceneContext.EditableScene;
		}

		private NamedAction[] BedMenuActions(BedConfig sceneContext)
		{
			// Bed menu
			return new[]
			{
				new NamedAction()
				{
					Title = "Save".Localize(),
					Action = async () =>
					{
						await ApplicationController.Instance.Tasks.Execute(this.SaveChanges);
					},
					IsEnabled = () => sceneContext.EditableScene
				},
				new NamedAction()
				{
					Title = "Save As".Localize(),
					Action = () => UiThread.RunOnIdle(OpenSaveAsWindow),
					IsEnabled = () => sceneContext.EditableScene
				},
				new NamedAction()
				{
					Title = "Export".Localize(),
					Action = () =>
					{
						UiThread.RunOnIdle(() =>
						{
							DialogWindow.Show(
								new ExportPrintItemPage(new[]
								{
									new FileSystemFileItem(sceneContext.EditContext.PartFilePath)
								}));
						});
					},
					IsEnabled = () => sceneContext.EditableScene
				},
				new NamedAction()
				{
					Title = "Publish".Localize(),
					Action = () =>
					{
						UiThread.RunOnIdle(() => DialogWindow.Show<PublishPartToMatterHackers>());
					},
					IsEnabled = () => sceneContext.EditableScene
				},
				new NamedAction()
				{
					Title = "Arrange All Parts".Localize(),
					Action = () =>
					{
						this.Scene.AutoArrangeChildren(this);
					},
					IsEnabled = () => sceneContext.EditableScene
				},
				new NamedAction() { Title = "----" },
				new NamedAction()
				{
					Title = "Clear Bed".Localize(),
					Action = () =>
					{
						UiThread.RunOnIdle(() =>
						{
							sceneContext.ClearPlate().ConfigureAwait(false);
						});
					}
				}
			};
		}

		private void SceneContext_SceneLoaded(object sender, EventArgs e)
		{
			if (this.printerTabPage?.printerActionsBar?.sliceButton is GuiWidget sliceButton)
			{
				sliceButton.Enabled = sceneContext.EditableScene;
			}

			this.viewControls3D.modelViewButton.Enabled = sceneContext.EditableScene;
			this.Invalidate();
		}

		private void SceneContext_LoadedGCodeChanged(object sender, EventArgs e)
		{
			if (printerTabPage != null)
			{
				if (printerTabPage.gcode3DWidget != null)
				{
					// HACK: directly fire method which previously ran on SlicingDone event on PrintItemWrapper
					UiThread.RunOnIdle(() => printerTabPage.gcode3DWidget.CreateAndAddChildren(printer));
				}
			}
		}

		private GuiWidget CreateActionSeparator()
		{
			return new VerticalLine(60)
			{
				Margin = new BorderDouble(3, 2, 0, 2),
			};
		}

		private void ViewControls3D_TransformStateChanged(object sender, TransformStateChangedEventArgs e)
		{
			switch (e.TransformMode)
			{
				case ViewControls3DButtons.Rotate:
					this.TrackballTumbleWidget.TransformState = TrackBallController.MouseDownType.Rotation;
					break;

				case ViewControls3DButtons.Translate:
					this.TrackballTumbleWidget.TransformState = TrackBallController.MouseDownType.Translation;
					break;

				case ViewControls3DButtons.Scale:
					this.TrackballTumbleWidget.TransformState = TrackBallController.MouseDownType.Scale;
					break;

				case ViewControls3DButtons.PartSelect:
					this.TrackballTumbleWidget.TransformState = TrackBallController.MouseDownType.None;
					break;
			}
		}

		public void SelectAll()
		{
			Scene.ClearSelection();
			foreach (var child in Scene.Children.ToList())
			{
				Scene.AddToSelection(child);
			}
		}

		private void Draw_GlOpaqueContent(object sender, DrawEventArgs e)
		{
			if (CurrentSelectInfo.DownOnPart
				&& TrackballTumbleWidget.TransformState == TrackBallController.MouseDownType.None
				&& Keyboard.IsKeyDown(Keys.ShiftKey))
			{
				// draw marks on the bed to show that the part is constrained to x and y
				AxisAlignedBoundingBox selectedBounds = this.Scene.SelectedItem.GetAxisAlignedBoundingBox(Matrix4X4.Identity);

				var drawCenter = CurrentSelectInfo.PlaneDownHitPos;
				var drawColor = new Color(Color.Red, 20);
				bool zBuffer = false;

				for (int i = 0; i < 2; i++)
				{
					GLHelper.Render3DLine(World,
						drawCenter - new Vector3(-50, 0, 0),
						drawCenter - new Vector3(50, 0, 0), drawColor, zBuffer, 2);

					GLHelper.Render3DLine(World,
						drawCenter - new Vector3(0, -50, 0),
						drawCenter - new Vector3(0, 50, 0), drawColor, zBuffer, 2);

					drawColor = Color.Black;
					drawCenter.Z = 0;
					zBuffer = true;
				}
			}


			// This shows the BVH as rects around the scene items
			//Scene?.TraceData().RenderBvhRecursive(0, 3);

			if (sceneContext.LoadedGCode == null || sceneContext.GCodeRenderer == null || printerTabPage?.gcode3DWidget.Visible == false)
			{
				return;
			}

			sceneContext.Render3DLayerFeatures(e);
		}

		public override void OnKeyDown(KeyEventArgs keyEvent)
		{
			// this must be called first to ensure we get the correct Handled state
			base.OnKeyDown(keyEvent);

			if (!keyEvent.Handled)
			{
				switch (keyEvent.KeyCode)
				{
					case Keys.A:
						if (keyEvent.Control)
						{
							SelectAll();
							keyEvent.Handled = true;
							keyEvent.SuppressKeyPress = true;
						}
						break;

					case Keys.Z:
						if (keyEvent.Control)
						{
							this.Scene.UndoBuffer.Undo();
							keyEvent.Handled = true;
							keyEvent.SuppressKeyPress = true;
						}
						break;

					case Keys.Y:
						if (keyEvent.Control)
						{
							this.Scene.UndoBuffer.Redo();
							keyEvent.Handled = true;
							keyEvent.SuppressKeyPress = true;
						}
						break;

					case Keys.Delete:
					case Keys.Back:
						this.Scene.DeleteSelection(this);
						break;

					case Keys.Escape:
						if (CurrentSelectInfo.DownOnPart)
						{
							CurrentSelectInfo.DownOnPart = false;

							Scene.SelectedItem.Matrix = transformOnMouseDown;

							Scene.Invalidate();
						}
						break;
					case Keys.Space:
						this.Scene.ClearSelection();
						break;
				}
			}
		}

		public bool DragingPart
		{
			get { return CurrentSelectInfo.DownOnPart; }
		}

		public void AddUndoOperation(IUndoRedoCommand operation)
		{
			this.Scene.UndoBuffer.Add(operation);
		}

		#region DoBooleanTest
		Object3D booleanGroup;
		Vector3 offset = new Vector3();
		Vector3 direction = new Vector3(.11, .12, .13);
		Vector3 rotCurrent = new Vector3();
		Vector3 rotChange = new Vector3(.011, .012, .013);
		Vector3 scaleChange = new Vector3(.0011, .0012, .0013);
		Vector3 scaleCurrent = new Vector3(1, 1, 1);

		private void CreateBooleanTestGeometry(object sender, DrawEventArgs e)
		{
			try
			{
				booleanGroup = new Object3D();

				booleanGroup.Children.Add(new Object3D()
				{
					Mesh = ApplyBoolean(PolygonMesh.Csg.CsgOperations.Union, AxisAlignedBoundingBox.Union, new Vector3(100, 0, 20), "U")
				});

				booleanGroup.Children.Add(new Object3D()
				{
					Mesh = ApplyBoolean(PolygonMesh.Csg.CsgOperations.Subtract, null, new Vector3(100, 100, 20), "S")
				});

				booleanGroup.Children.Add(new Object3D()
				{
					Mesh = ApplyBoolean(PolygonMesh.Csg.CsgOperations.Intersect, AxisAlignedBoundingBox.Intersection, new Vector3(100, 200, 20), "I")
				});

				offset += direction;
				rotCurrent += rotChange;
				scaleCurrent += scaleChange;

				this.Scene.Children.Modify(list =>
				{
					list.Add(booleanGroup);
				});
			}
			catch (Exception e2)
			{
				string text = e2.Message;
				int a = 0;
			}
		}

		private Mesh ApplyBoolean(Func<Mesh, Mesh, Mesh> meshOpperation, Func<AxisAlignedBoundingBox, AxisAlignedBoundingBox, AxisAlignedBoundingBox> aabbOpperation, Vector3 centering, string opp)
		{
			Mesh boxA = PlatonicSolids.CreateCube(40, 40, 40);
			//boxA = PlatonicSolids.CreateIcosahedron(35);
			boxA.Translate(centering);
			Mesh boxB = PlatonicSolids.CreateCube(40, 40, 40);
			//boxB = PlatonicSolids.CreateIcosahedron(35);

			for (int i = 0; i < 3; i++)
			{
				if (Math.Abs(direction[i] + offset[i]) > 10)
				{
					direction[i] = direction[i] * -1.00073112;
				}
			}

			for (int i = 0; i < 3; i++)
			{
				if (Math.Abs(rotChange[i] + rotCurrent[i]) > 6)
				{
					rotChange[i] = rotChange[i] * -1.000073112;
				}
			}

			for (int i = 0; i < 3; i++)
			{
				if (scaleChange[i] + scaleCurrent[i] > 1.1 || scaleChange[i] + scaleCurrent[i] < .9)
				{
					scaleChange[i] = scaleChange[i] * -1.000073112;
				}
			}

			Vector3 offsetB = offset + centering;
			// switch to the failing offset
			//offsetB = new Vector3(105.240172225344, 92.9716306394062, 18.4619570261172);
			//rotCurrent = new Vector3(4.56890223673623, -2.67874102322035, 1.02768848238523);
			//scaleCurrent = new Vector3(1.07853517569753, 0.964980885267323, 1.09290934544604);
			Debug.WriteLine("t" + offsetB.ToString() + " r" + rotCurrent.ToString() + " s" + scaleCurrent.ToString() + " " + opp);
			Matrix4X4 transformB = Matrix4X4.CreateScale(scaleCurrent) * Matrix4X4.CreateRotation(rotCurrent) * Matrix4X4.CreateTranslation(offsetB);
			boxB.Transform(transformB);

			Mesh meshToAdd = meshOpperation(boxA, boxB);
			meshToAdd.CleanAndMergMesh(CancellationToken.None);

			if (aabbOpperation != null)
			{
				AxisAlignedBoundingBox boundsA = boxA.GetAxisAlignedBoundingBox();
				AxisAlignedBoundingBox boundsB = boxB.GetAxisAlignedBoundingBox();
				AxisAlignedBoundingBox boundsAdd = meshToAdd.GetAxisAlignedBoundingBox();

				AxisAlignedBoundingBox boundsResult = aabbOpperation(boundsA, boundsB);
			}

			return meshToAdd;
		}

		private void RemoveBooleanTestGeometry(object sender, DrawEventArgs e)
		{
			if (this.Scene.Children.Contains(booleanGroup))
			{
				this.Scene.Children.Remove(booleanGroup);
				UiThread.RunOnIdle(() => Invalidate(), 1.0 / 30.0);
			}
		}
		#endregion DoBooleanTest

		public enum AutoRotate { Enabled, Disabled };

		public bool DisplayAllValueData { get; set; }

		public override void OnClosed(ClosedEventArgs e)
		{
			viewControls3D.TransformStateChanged -= ViewControls3D_TransformStateChanged;
			sceneContext.LoadedGCodeChanged -= SceneContext_LoadedGCodeChanged;
			this.Scene.SelectionChanged -= Scene_SelectionChanged;
			this.InteractionLayer.DrawGlOpaqueContent -= Draw_GlOpaqueContent;
			this.sceneContext.SceneLoaded -= SceneContext_SceneLoaded;

			if (meshViewerWidget != null)
			{
				meshViewerWidget.AfterDraw -= AfterDraw3DContent;
			}

			unregisterEvents?.Invoke(this, null);
			base.OnClosed(e);
		}

		private GuiWidget topMostParent;

		private PlaneShape bedPlane = new PlaneShape(Vector3.UnitZ, 0, null);

		public bool DragOperationActive { get; private set; }

		public InsertionGroup DragDropObject { get; private set; }
		public ILibraryContentStream SceneReplacement { get; private set; }

		/// <summary>
		/// Provides a View3DWidget specific drag implementation
		/// </summary>
		/// <param name="screenSpaceMousePosition">The screen space mouse position.</param>
		public void ExternalDragOver(Vector2 screenSpaceMousePosition)
		{
			if (this.HasBeenClosed)
			{
				return;
			}

			// If the mouse is within the MeshViewer process the Drag move
			var meshViewerPosition = this.meshViewerWidget.TransformToScreenSpace(meshViewerWidget.LocalBounds);
			if (meshViewerPosition.Contains(screenSpaceMousePosition))
			{
				// If already started, process drag move
				if (this.DragOperationActive)
				{
					this.DragOver(screenSpaceMousePosition);
				}
				else
				{
					// Otherwise begin an externally started DragDropOperation hard-coded to use LibraryView->SelectedItems

					this.StartDragDrop(
						// Project from ListViewItem to ILibraryItem
						ApplicationController.Instance.Library.ActiveViewWidget.SelectedItems.Select(l => l.Model),
						screenSpaceMousePosition);
				}

				
			}
		}

		private void DragOver(Vector2 screenSpaceMousePosition)
		{
			// Move the object being dragged
			if (this.DragOperationActive
				&& this.DragDropObject != null)
			{
				// Move the DropDropObject the target item
				DragSelectedObject(localMousePostion: this.TransformFromParentSpace(topMostParent, screenSpaceMousePosition));
			}
		}

		private void StartDragDrop(IEnumerable<ILibraryItem> items, Vector2 screenSpaceMousePosition, bool trackSourceFiles = false)
		{
			this.DragOperationActive = true;

			var firstItem = items.FirstOrDefault();

			if ((firstItem is ILibraryContentStream contentStream
				&& contentStream.ContentType == "gcode")
				|| firstItem is SceneReplacementFileItem)
			{
				DragDropObject = null;
				this.SceneReplacement = firstItem as ILibraryContentStream;

				// TODO: Figure out a mechanism to disable View3DWidget with dark overlay, displaying something like "Switch to xxx.gcode", make disappear on mouseLeaveBounds and dragfinish
				this.InteractionLayer.BackgroundColor = new Color(Color.Black, 200);

				return;
			}

			// Set the hitplane to the bed plane
			CurrentSelectInfo.HitPlane = bedPlane;

			var insertionGroup = new InsertionGroup(
				items,
				this,
				this.Scene,
				sceneContext.BedCenter,
				() => this.DragOperationActive,
				trackSourceFiles);

			// Find intersection position of the mouse with the bed plane
			var intersectInfo = GetIntersectPosition(screenSpaceMousePosition);
			if (intersectInfo != null)
			{
				// Set the initial transform on the inject part to the current transform mouse position
				var sourceItemBounds = insertionGroup.GetAxisAlignedBoundingBox(Matrix4X4.Identity);
				var center = sourceItemBounds.Center;

				insertionGroup.Matrix *= Matrix4X4.CreateTranslation(-center.X, -center.Y, -sourceItemBounds.minXYZ.Z);
				insertionGroup.Matrix *= Matrix4X4.CreateTranslation(new Vector3(intersectInfo.HitPosition));

				CurrentSelectInfo.PlaneDownHitPos = intersectInfo.HitPosition;
				CurrentSelectInfo.LastMoveDelta = Vector3.Zero;
			}

			this.deferEditorTillMouseUp = true;

			// Add item to scene and select it
			this.Scene.Children.Modify(list =>
			{
				list.Add(insertionGroup);
			});
			Scene.SelectedItem = insertionGroup;

			this.DragDropObject = insertionGroup;
		}

		internal void FinishDrop(bool mouseUpInBounds)
		{
			if (this.DragOperationActive)
			{
				this.InteractionLayer.BackgroundColor = Color.Transparent;
				this.DragOperationActive = false;

				if (mouseUpInBounds)
				{
					if (this.DragDropObject == null
						&& this.SceneReplacement != null)
					{
						// Drop handler for special case of GCode or similar (change loaded scene to new context)
						sceneContext.LoadContent(
							new EditContext()
							{
								SourceItem = this.SceneReplacement,
								// No content store for GCode, otherwise PlatingHistory
								ContentStore = (this.SceneReplacement.ContentType == "gcode") ? null : ApplicationController.Instance.Library.PlatingHistory
							}).ConfigureAwait(false);

						this.SceneReplacement = null;
					}
					else if (this.DragDropObject.ContentAcquired)
					{

						// Drop handler for InsertionGroup - all normal content
						this.viewControls3D.modelViewButton.Enabled = true;
						this.DragDropObject.Collapse();
					}
				}
				else
				{
					this.Scene.Children.Modify(list => list.Remove(this.DragDropObject));
					this.Scene.ClearSelection();
				}

				this.DragDropObject = null;

				this.deferEditorTillMouseUp = false;
				Scene_SelectionChanged(null, null);

				Scene.Invalidate();

				// Set focus to View3DWidget after drag-drop
				UiThread.RunOnIdle(this.Focus);

			}
		}

		public override void OnLoad(EventArgs args)
		{
			topMostParent = this.TopmostParent();

			// Set reference on show
			var dragDropData = ApplicationController.Instance.DragDropData;
			dragDropData.View3DWidget = this;
			dragDropData.SceneContext = sceneContext;

			base.OnLoad(args);
		}

		public override void OnDraw(Graphics2D graphics2D)
		{
			var selectedItem = Scene.SelectedItem;

			if (Scene.HasSelection
				&& selectedItem != null)
			{

				foreach (InteractionVolume volume in this.InteractionLayer.InteractionVolumes)
				{
					volume.SetPosition(selectedItem);
				}
			}

			hasDrawn = true;

			base.OnDraw(graphics2D);
		}

		private void AfterDraw3DContent(object sender, DrawEventArgs e)
		{
			if (DragSelectionInProgress)
			{
				var selectionRectangle = new RectangleDouble(DragSelectionStartPosition, DragSelectionEndPosition);
				e.graphics2D.Rectangle(selectionRectangle, Color.Red);
			}
		}

		bool foundTriangleInSelectionBounds;
		private void DoRectangleSelection(DrawEventArgs e)
		{
			var allResults = new List<BvhIterator>();

			var matchingSceneChildren = Scene.Children.Where(item =>
			{
				foundTriangleInSelectionBounds = false;

				// Filter the IPrimitive trace data finding matches as defined in InSelectionBounds
				var filteredResults = item.TraceData().Filter(InSelectionBounds);

				// Accumulate all matching BvhIterator results for debug rendering
				allResults.AddRange(filteredResults);

				return foundTriangleInSelectionBounds;
			});

			// Apply selection
			if (matchingSceneChildren.Any())
			{
				// If we are actually doing the selection rather than debugging the data
				if (e == null)
				{
					Scene.ClearSelection();

					foreach (var sceneItem in matchingSceneChildren.ToList())
					{
						Scene.AddToSelection(sceneItem);
					}
				}
				else
				{
					InteractionLayer.RenderBounds(e, World, allResults);
				}
			}
		}

		private bool InSelectionBounds(BvhIterator x)
		{
			var selectionRectangle = new RectangleDouble(DragSelectionStartPosition, DragSelectionEndPosition);

			Vector2[] traceBottoms = new Vector2[4];
			Vector2[] traceTops = new Vector2[4];

			if (foundTriangleInSelectionBounds)
			{
				return false;
			}
			if (x.Bvh is TriangleShape tri)
			{
				// check if any vertex in screen rect
				// calculate all the top and bottom screen positions
				for (int i = 0; i < 3; i++)
				{
					Vector3 bottomStartPosition = Vector3.Transform(tri.GetVertex(i), x.TransformToWorld);
					traceBottoms[i] = this.World.GetScreenPosition(bottomStartPosition);
				}

				for (int i = 0; i < 3; i++)
				{
					if (selectionRectangle.ClipLine(traceBottoms[i], traceBottoms[(i + 1) % 3]))
					{
						foundTriangleInSelectionBounds = true;
						return true;
					}
				}
			}
			else
			{
				// calculate all the top and bottom screen positions
				for (int i = 0; i < 4; i++)
				{
					Vector3 bottomStartPosition = Vector3.Transform(x.Bvh.GetAxisAlignedBoundingBox().GetBottomCorner(i), x.TransformToWorld);
					traceBottoms[i] = this.World.GetScreenPosition(bottomStartPosition);

					Vector3 topStartPosition = Vector3.Transform(x.Bvh.GetAxisAlignedBoundingBox().GetTopCorner(i), x.TransformToWorld);
					traceTops[i] = this.World.GetScreenPosition(topStartPosition);
				}

				RectangleDouble.OutCode allPoints = RectangleDouble.OutCode.Inside;
				// check if we are inside all the points
				for (int i = 0; i < 4; i++)
				{
					allPoints |= selectionRectangle.ComputeOutCode(traceBottoms[i]);
					allPoints |= selectionRectangle.ComputeOutCode(traceTops[i]);
				}

				if (allPoints == RectangleDouble.OutCode.Surrounded)
				{
					return true;
				}

				for (int i = 0; i < 4; i++)
				{
					if (selectionRectangle.ClipLine(traceBottoms[i], traceBottoms[(i + 1) % 4])
						|| selectionRectangle.ClipLine(traceTops[i], traceTops[(i + 1) % 4])
						|| selectionRectangle.ClipLine(traceTops[i], traceBottoms[i]))
					{
						return true;
					}
				}
			}

			return false;
		}

		private ViewControls3DButtons? activeButtonBeforeMouseOverride = null;

		public override void OnMouseDown(MouseEventArgs mouseEvent)
		{
			// Show transform override
			if (activeButtonBeforeMouseOverride == null && mouseEvent.Button == MouseButtons.Right)
			{
				activeButtonBeforeMouseOverride = viewControls3D.ActiveButton;
				viewControls3D.ActiveButton = ViewControls3DButtons.Rotate;
			}
			else if (activeButtonBeforeMouseOverride == null && mouseEvent.Button == MouseButtons.Middle)
			{
				activeButtonBeforeMouseOverride = viewControls3D.ActiveButton;
				viewControls3D.ActiveButton = ViewControls3DButtons.Translate;
			}

			if(mouseEvent.Button == MouseButtons.Right ||
				mouseEvent.Button == MouseButtons.Middle)
			{
				meshViewerWidget.SuppressUiVolumes = true;
			}

			autoRotating = false;
			base.OnMouseDown(mouseEvent);

			if (this.TrackballTumbleWidget.UnderMouseState == UnderMouseState.FirstUnderMouse)
			{
				if (mouseEvent.Button == MouseButtons.Left
					&&
					(ModifierKeys == Keys.Shift || ModifierKeys == Keys.Control)
					|| (
						this.TrackballTumbleWidget.TransformState == TrackBallController.MouseDownType.None
						&& ModifierKeys != Keys.Control
						&& ModifierKeys != Keys.Alt))
				{
					if (!this.InteractionLayer.MouseDownOnInteractionVolume)
					{
						meshViewerWidget.SuppressUiVolumes = true;

						IntersectInfo info = new IntersectInfo();

						IObject3D hitObject = FindHitObject3D(mouseEvent.Position, ref info);
						if (hitObject == null)
						{
							if (Scene.HasSelection)
							{
								Scene.ClearSelection();
							}

							// start a selection rect
							DragSelectionStartPosition = mouseEvent.Position - OffsetToMeshViewerWidget();
							DragSelectionEndPosition = DragSelectionStartPosition;
							DragSelectionInProgress = true;
						}
						else
						{
							CurrentSelectInfo.HitPlane = new PlaneShape(Vector3.UnitZ, CurrentSelectInfo.PlaneDownHitPos.Z, null);

							if (hitObject != Scene.SelectedItem)
							{
								if (Scene.SelectedItem == null)
								{
									// No selection exists
									Scene.SelectedItem = hitObject;
								}
								else if ((ModifierKeys == Keys.Shift || ModifierKeys == Keys.Control)
									&& !Scene.SelectedItem.Children.Contains(hitObject))
								{
									Scene.AddToSelection(hitObject);
								}
								else if (Scene.SelectedItem == hitObject || Scene.SelectedItem.Children.Contains(hitObject))
								{
									// Selection should not be cleared and drag should occur
								}
								else if (ModifierKeys != Keys.Shift)
								{
									Scene.SelectedItem = hitObject;
								}

								Invalidate();
							}

							transformOnMouseDown = Scene.SelectedItem.Matrix;

							Invalidate();
							CurrentSelectInfo.DownOnPart = true;

							AxisAlignedBoundingBox selectedBounds = this.Scene.SelectedItem.GetAxisAlignedBoundingBox(Matrix4X4.Identity);

							if (info.HitPosition.X < selectedBounds.Center.X)
							{
								if (info.HitPosition.Y < selectedBounds.Center.Y)
								{
									CurrentSelectInfo.HitQuadrant = HitQuadrant.LB;
								}
								else
								{
									CurrentSelectInfo.HitQuadrant = HitQuadrant.LT;
								}
							}
							else
							{
								if (info.HitPosition.Y < selectedBounds.Center.Y)
								{
									CurrentSelectInfo.HitQuadrant = HitQuadrant.RB;
								}
								else
								{
									CurrentSelectInfo.HitQuadrant = HitQuadrant.RT;
								}
							}
						}
					}
				}
			}
		}

		public IntersectInfo GetIntersectPosition(Vector2 screenSpacePosition)
		{
			//Vector2 meshViewerWidgetScreenPosition = meshViewerWidget.TransformFromParentSpace(this, new Vector2(mouseEvent.X, mouseEvent.Y));

			// Translate to local
			Vector2 localPosition = this.TransformFromScreenSpace(screenSpacePosition);

			Ray ray = this.World.GetRayForLocalBounds(localPosition);

			return CurrentSelectInfo.HitPlane.GetClosestIntersection(ray);
		}

		public void DragSelectedObject(Vector2 localMousePostion)
		{
			Vector2 meshViewerWidgetScreenPosition = meshViewerWidget.TransformFromParentSpace(this, localMousePostion);
			Ray ray = this.World.GetRayForLocalBounds(meshViewerWidgetScreenPosition);

			IntersectInfo info = CurrentSelectInfo.HitPlane.GetClosestIntersection(ray);
			if (info != null)
			{
				// move the mesh back to the start position
				{
					Matrix4X4 totalTransform = Matrix4X4.CreateTranslation(new Vector3(-CurrentSelectInfo.LastMoveDelta));
					Scene.SelectedItem.Matrix *= totalTransform;

					// Invalidate the item to account for the position change
					Scene.SelectedItem.Invalidate();
				}

				Vector3 delta = info.HitPosition - CurrentSelectInfo.PlaneDownHitPos;

				double snapGridDistance = this.InteractionLayer.SnapGridDistance;
				if (snapGridDistance > 0)
				{
					// snap this position to the grid
					AxisAlignedBoundingBox selectedBounds = this.Scene.SelectedItem.GetAxisAlignedBoundingBox(Matrix4X4.Identity);

					double xSnapOffset = selectedBounds.minXYZ.X;
					// snap the x position
					if (CurrentSelectInfo.HitQuadrant == HitQuadrant.RB
						|| CurrentSelectInfo.HitQuadrant == HitQuadrant.RT)
					{
						// switch to the other side
						xSnapOffset = selectedBounds.maxXYZ.X;
					}
					double xToSnap = xSnapOffset + delta.X;

					double snappedX = ((int)((xToSnap / snapGridDistance) + .5)) * snapGridDistance;
					delta.X = snappedX - xSnapOffset;

					double ySnapOffset = selectedBounds.minXYZ.Y;
					// snap the y position
					if (CurrentSelectInfo.HitQuadrant == HitQuadrant.LT
						|| CurrentSelectInfo.HitQuadrant == HitQuadrant.RT)
					{
						// switch to the other side
						ySnapOffset = selectedBounds.maxXYZ.Y;
					}
					double yToSnap = ySnapOffset + delta.Y;

					double snappedY = ((int)((yToSnap / snapGridDistance) + .5)) * snapGridDistance;
					delta.Y = snappedY - ySnapOffset;
				}

				// if the shift key is down only move on the major axis of x or y
				if(Keyboard.IsKeyDown(Keys.ShiftKey))
				{
					if(Math.Abs(delta.X) < Math.Abs(delta.Y))
					{
						delta.X = 0;
					}
					else
					{
						delta.Y = 0;
					}
				}

				// move the mesh back to the new position
				{
					Matrix4X4 totalTransform = Matrix4X4.CreateTranslation(new Vector3(delta));

					Scene.SelectedItem.Matrix *= totalTransform;

					CurrentSelectInfo.LastMoveDelta = delta;
				}

				Invalidate();
			}
		}

		public override void OnMouseMove(MouseEventArgs mouseEvent)
		{
			// File system Drop validation
			mouseEvent.AcceptDrop = this.AllowDragDrop()
					&& mouseEvent.DragFiles?.Count > 0
					&& mouseEvent.DragFiles.TrueForAll(filePath => ApplicationController.Instance.IsLoadableFile(filePath));

			// View3DWidgets Filesystem DropDrop handler
			if (mouseEvent.AcceptDrop
				&& this.PositionWithinLocalBounds(mouseEvent.X, mouseEvent.Y))
			{
				if (this.DragOperationActive)
				{
					DragOver(screenSpaceMousePosition: this.TransformToScreenSpace(mouseEvent.Position));
				}
				else
				{ 
					// Project DragFiles to IEnumerable<FileSystemFileItem>
					this.StartDragDrop(
						mouseEvent.DragFiles.Select(path => new FileSystemFileItem(path)),
						screenSpaceMousePosition: this.TransformToScreenSpace(mouseEvent.Position),
						trackSourceFiles: true);
				}
			}

			if (CurrentSelectInfo.DownOnPart && this.TrackballTumbleWidget.TransformState == TrackBallController.MouseDownType.None)
			{
				DragSelectedObject(new Vector2(mouseEvent.X, mouseEvent.Y));
			}

			if (DragSelectionInProgress)
			{
				DragSelectionEndPosition = mouseEvent.Position - OffsetToMeshViewerWidget();
				DragSelectionEndPosition = new Vector2(
					Math.Max(Math.Min(DragSelectionEndPosition.X, meshViewerWidget.LocalBounds.Right), meshViewerWidget.LocalBounds.Left),
					Math.Max(Math.Min(DragSelectionEndPosition.Y, meshViewerWidget.LocalBounds.Top), meshViewerWidget.LocalBounds.Bottom));
				Invalidate();
			}

			base.OnMouseMove(mouseEvent);
		}

		Vector2 OffsetToMeshViewerWidget()
		{
			List<GuiWidget> parents = new List<GuiWidget>();
			GuiWidget parent = meshViewerWidget.Parent;
			while (parent != this)
			{
				parents.Add(parent);
				parent = parent.Parent;
			}
			Vector2 offset = new Vector2();
			for(int i=parents.Count-1; i>=0; i--)
			{
				offset += parents[i].OriginRelativeParent;
			}
			return offset;
		}

		public void ResetView()
		{
			this.TrackballTumbleWidget.ZeroVelocity();

			var world = this.World;

			world.Reset();
			world.Scale = .03;
			world.Translate(-new Vector3(sceneContext.BedCenter));
			world.Rotate(Quaternion.FromEulerAngles(new Vector3(0, 0, -MathHelper.Tau / 16)));
			world.Rotate(Quaternion.FromEulerAngles(new Vector3(MathHelper.Tau * .19, 0, 0)));
		}

		public override void OnMouseUp(MouseEventArgs mouseEvent)
		{
			if (this.DragOperationActive)
			{
				this.FinishDrop(mouseUpInBounds: true);
			}

			if (this.TrackballTumbleWidget.TransformState == TrackBallController.MouseDownType.None)
			{
				if (Scene.SelectedItem != null
					&& CurrentSelectInfo.DownOnPart
					&& CurrentSelectInfo.LastMoveDelta != Vector3.Zero)
				{
					InteractionLayer.AddTransformSnapshot(transformOnMouseDown);
				}
				else if (DragSelectionInProgress)
				{
					DoRectangleSelection(null);
					DragSelectionInProgress = false;
				}
			}

			meshViewerWidget.SuppressUiVolumes = false;

			CurrentSelectInfo.DownOnPart = false;

			if (activeButtonBeforeMouseOverride != null)
			{
				viewControls3D.ActiveButton = (ViewControls3DButtons)activeButtonBeforeMouseOverride;
				activeButtonBeforeMouseOverride = null;
			}

			base.OnMouseUp(mouseEvent);

			if (deferEditorTillMouseUp)
			{
				this.deferEditorTillMouseUp = false;
				Scene_SelectionChanged(null, null);
			}
		}

		internal GuiWidget AddAlignControls()
		{
			var widget = new IgnoredPopupWidget()
			{
				HAnchor = HAnchor.Fit,
				VAnchor = VAnchor.Fit,
				BackgroundColor = Color.White,
				Padding = new BorderDouble(5, 5, 5, 0)
			};

			FlowLayoutWidget buttonPanel = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				VAnchor = VAnchor.Fit,
				HAnchor = HAnchor.Fit,
			};
			widget.AddChild(buttonPanel);

			string[] axisNames = new string[] { "X", "Y", "Z" };
			for (int axisIndex = 0; axisIndex < 3; axisIndex++)
			{
				FlowLayoutWidget alignButtons = new FlowLayoutWidget(FlowDirection.LeftToRight)
				{
					HAnchor = HAnchor.Fit,
					Padding = new BorderDouble(5)
				};
				buttonPanel.AddChild(alignButtons);

				alignButtons.AddChild(new TextWidget(axisNames[axisIndex])
				{
					VAnchor = VAnchor.Center,
					Margin = new BorderDouble(0, 0, 3, 0)
				});

				alignButtons.AddChild(CreateAlignButton(axisIndex, AxisAlignment.Min, "Min"));
				alignButtons.AddChild(new HorizontalSpacer());
				alignButtons.AddChild(CreateAlignButton(axisIndex, AxisAlignment.Center, "Center"));
				alignButtons.AddChild(new HorizontalSpacer());
				alignButtons.AddChild(CreateAlignButton(axisIndex, AxisAlignment.Max, "Max"));
				alignButtons.AddChild(new HorizontalSpacer());
			}

			var dualExtrusionAlignButton = theme.MenuButtonFactory.Generate("Align for Dual Extrusion".Localize());
			dualExtrusionAlignButton.Margin = new BorderDouble(21, 0);
			dualExtrusionAlignButton.HAnchor = HAnchor.Left;
			buttonPanel.AddChild(dualExtrusionAlignButton);

			AddAlignDelegates(0, AxisAlignment.SourceCoordinateSystem, dualExtrusionAlignButton);

			return widget;
		}

		internal enum AxisAlignment { Min, Center, Max, SourceCoordinateSystem };
		private GuiWidget CreateAlignButton(int axisIndex, AxisAlignment alignment, string lable)
		{
			var smallMarginButtonFactory = theme.MenuButtonFactory;
			var alignButton = smallMarginButtonFactory.Generate(lable);
			alignButton.Margin = new BorderDouble(3, 0);

			AddAlignDelegates(axisIndex, alignment, alignButton);

			return alignButton;
		}

		private void AddAlignDelegates(int axisIndex, AxisAlignment alignment, Button alignButton)
		{
			alignButton.Click += (sender, e) =>
			{
				if (Scene.HasSelection)
				{
					var transformDatas = GetTransforms(axisIndex, alignment);
					this.Scene.UndoBuffer.AddAndDo(new TransformCommand(transformDatas));

					//Scene.SelectedItem.MaterialIndex = extruderIndexCanPassToClick;
					Scene.Invalidate();
				}
			};

			alignButton.MouseEnter += (s2, e2) =>
			{
				if (Scene.HasSelection)
				{
					// make a preview of the new positions
					var transformDatas = GetTransforms(axisIndex, alignment);
					Scene.Children.Modify((list) =>
					{
						foreach (var transform in transformDatas)
						{
							var copy = transform.TransformedObject.Clone();
							copy.Matrix = transform.RedoTransform;
							copy.Color = new Color(Color.Gray, 126);
							list.Add(copy);
						}
					});
				}
			};

			alignButton.MouseLeave += (s3, e3) =>
			{
				if (Scene.HasSelection)
				{
					// clear the preview of the new positions
					Scene.Children.Modify((list) =>
					{
						for(int i=list.Count-1; i>=0; i--)
						{
							if (list[i].Color.Alpha0To255 == 126)
							{
								list.RemoveAt(i);
							}
						}
					});
				}
			};
		}

		private List<TransformData> GetTransforms(int axisIndex, AxisAlignment alignment)
		{
			var transformDatas = new List<TransformData>();
			var totalAABB = Scene.SelectedItem.GetAxisAlignedBoundingBox(Matrix4X4.Identity);

			Vector3 firstSourceOrigin = new Vector3(double.MaxValue, double.MaxValue, double.MaxValue);

			// move the objects to the right place
			foreach (var child in Scene.SelectedItem.Children)
			{
				var childAABB = child.GetAxisAlignedBoundingBox(Scene.SelectedItem.Matrix);
				var offset = new Vector3();
				switch (alignment)
				{
					case AxisAlignment.Min:
						offset[axisIndex] = totalAABB.minXYZ[axisIndex] - childAABB.minXYZ[axisIndex];
						break;

					case AxisAlignment.Center:
						offset[axisIndex] = totalAABB.Center[axisIndex] - childAABB.Center[axisIndex];
						break;

					case AxisAlignment.Max:
						offset[axisIndex] = totalAABB.maxXYZ[axisIndex] - childAABB.maxXYZ[axisIndex];
						break;

					case AxisAlignment.SourceCoordinateSystem:
						{
							// move the object back to the origin
							offset = -Vector3.Transform(Vector3.Zero, child.Matrix);

							// figure out how to move it back to the start center
							if(firstSourceOrigin.X == double.MaxValue)
							{
								firstSourceOrigin = -offset;
							}

							offset += firstSourceOrigin;
						}
						break;
				}
				transformDatas.Add(new TransformData()
				{
					TransformedObject = child,
					RedoTransform = child.Matrix * Matrix4X4.CreateTranslation(offset),
					UndoTransform = child.Matrix,
				});
			}

			return transformDatas;
		}

		internal GuiWidget AddMaterialControls()
		{
			var widget = new IgnoredPopupWidget()
			{
				HAnchor = HAnchor.Fit,
				VAnchor = VAnchor.Fit,
				BackgroundColor = Color.White,
				Padding = new BorderDouble(0, 5, 5, 0)
			};

			FlowLayoutWidget buttonPanel = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				VAnchor = VAnchor.Fit,
				HAnchor = HAnchor.Fit,
			};
			widget.AddChild(buttonPanel);

			materialButtons.Clear();
			int extruderCount = 4;
			for (int extruderIndex = 0; extruderIndex < extruderCount; extruderIndex++)
			{
				FlowLayoutWidget colorSelectionContainer = new FlowLayoutWidget(FlowDirection.LeftToRight)
				{
					HAnchor = HAnchor.Fit,
					Padding = new BorderDouble(5)
				};
				buttonPanel.AddChild(colorSelectionContainer);

				string materialLabelText = string.Format("{0} {1}", "Material".Localize(), extruderIndex + 1);

				RadioButton materialSelection = new RadioButton(materialLabelText, textColor: Color.Black);
				materialButtons.Add(materialSelection);
				materialSelection.SiblingRadioButtonList = materialButtons;
				colorSelectionContainer.AddChild(materialSelection);
				colorSelectionContainer.AddChild(new HorizontalSpacer());
				int extruderIndexCanPassToClick = extruderIndex;
				materialSelection.Click += (sender, e) =>
				{
					if (Scene.HasSelection)
					{
						Scene.SelectedItem.MaterialIndex = extruderIndexCanPassToClick;
						Scene.Invalidate();

						// "View 3D Overflow Menu" // the menu to click on
						// "Materials Option" // the item to highlight
						//HelpSystem.
					}
				};

				colorSelectionContainer.AddChild(new GuiWidget(16, 16)
				{
					BackgroundColor = MatterialRendering.Color(extruderIndex),
					Margin = new BorderDouble(5, 0, 0, 0)
				});
			}

			return widget;
		}

		// TODO: Consider if we should always allow DragDrop or if we should prevent during printer or other scenarios
		private bool AllowDragDrop() => true;

		private void AutoSpin()
		{
			if (!HasBeenClosed && autoRotating)
			{
				// add it back in to keep it running.
				UiThread.RunOnIdle(AutoSpin, .04);

				if ((!timeSinceLastSpin.IsRunning || timeSinceLastSpin.ElapsedMilliseconds > 50)
					&& hasDrawn)
				{
					hasDrawn = false;
					timeSinceLastSpin.Restart();

					Quaternion currentRotation = this.World.RotationMatrix.GetRotation();
					Quaternion invertedRotation = Quaternion.Invert(currentRotation);

					Quaternion rotateAboutZ = Quaternion.FromEulerAngles(new Vector3(0, 0, .01));
					rotateAboutZ = invertedRotation * rotateAboutZ * currentRotation;
					this.World.Rotate(rotateAboutZ);
					Invalidate();
				}
			}
		}

		private void Scene_SelectionChanged(object sender, EventArgs e)
		{
			if (!Scene.HasSelection)
			{
				selectedObjectContainer.Visible = false;
				return;
			}

			if (deferEditorTillMouseUp)
			{
				return;
			}

			var selectedItem = Scene.SelectedItem;

			if (materialButtons?.Count > 0)
			{
				bool setSelection = false;
				// Set the material selector to have the correct material button selected
				for (int i = 0; i < materialButtons.Count; i++)
				{
					if (selectedItem.MaterialIndex == i)
					{
						((RadioButton)materialButtons[i]).Checked = true;
						setSelection = true;
					}
				}

				if(!setSelection)
				{
					((RadioButton)materialButtons[0]).Checked = true;
				}
			}

			selectedObjectPanel.SetActiveItem(selectedItem);
		}

		private void ShowObjectEditor(IObject3DEditor editor)
		{
			editorPanel.CloseAllChildren();

			var newEditor = editor.Create(Scene.SelectedItem, this, this.theme);
			newEditor.HAnchor = HAnchor.Stretch;
			newEditor.VAnchor = VAnchor.Fit;

			editorPanel.AddChild(newEditor);
		}

		private void DrawStuffForSelectedPart(Graphics2D graphics2D)
		{
			if (Scene.HasSelection)
			{
				AxisAlignedBoundingBox selectedBounds = Scene.SelectedItem.GetAxisAlignedBoundingBox(Scene.SelectedItem.Matrix);
				Vector3 boundsCenter = selectedBounds.Center;
				Vector3 centerTop = new Vector3(boundsCenter.X, boundsCenter.Y, selectedBounds.maxXYZ.Z);

				Vector2 centerTopScreenPosition = this.World.GetScreenPosition(centerTop);
				centerTopScreenPosition = meshViewerWidget.TransformToParentSpace(this, centerTopScreenPosition);
				//graphics2D.Circle(screenPosition.x, screenPosition.y, 5, Color.Cyan);

				VertexStorage zArrow = new VertexStorage();
				zArrow.MoveTo(-6, -2);
				zArrow.curve3(0, -4);
				zArrow.LineTo(6, -2);
				zArrow.LineTo(0, 12);
				zArrow.LineTo(-6, -2);
			}
		}

		private async void LoadAndAddPartsToPlate(string[] filesToLoad)
		{
			if (filesToLoad != null && filesToLoad.Length > 0)
			{
				await Task.Run(() => loadAndAddPartsToPlate(filesToLoad));

				if (HasBeenClosed)
				{
					return;
				}

				bool addingOnlyOneItem = Scene.Children.Count == Scene.Children.Count + 1;

				if (Scene.HasChildren())
				{
					if (addingOnlyOneItem)
					{
						// if we are only adding one part to the plate set the selection to it
						Scene.SelectLastChild();
					}
				}

				Scene.Invalidate();
				this.Invalidate();
			}
		}

		private async Task loadAndAddPartsToPlate(string[] filesToLoadIncludingZips)
		{
			Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

			if (filesToLoadIncludingZips?.Any() == true)
			{
				List<string> filesToLoad = new List<string>();
				foreach (string loadedFileName in filesToLoadIncludingZips)
				{
					string extension = Path.GetExtension(loadedFileName).ToUpper();
					if ((extension != "" && MeshFileIo.ValidFileExtensions().Contains(extension)))
					{
						filesToLoad.Add(loadedFileName);
					}
					else if (extension == ".ZIP")
					{
						List<PrintItem> partFiles = ProjectFileHandler.ImportFromProjectArchive(loadedFileName);
						if (partFiles != null)
						{
							foreach (PrintItem part in partFiles)
							{
								filesToLoad.Add(part.FileLocation);
							}
						}
					}
				}

				string progressMessage = "Loading Parts...".Localize();

				var itemCache = new Dictionary<string, IObject3D>();

				foreach (string filePath in filesToLoad)
				{
					var libraryItem = new FileSystemFileItem(filePath);

					IObject3D object3D = null;

					await ApplicationController.Instance.Tasks.Execute(async (progressReporter, cancelationToken) =>
					{
						var progressStatus = new ProgressStatus()
						{
							Status = "Loading ".Localize() + Path.GetFileName(filePath),
						};

						progressReporter.Report(progressStatus);

						object3D = await libraryItem.CreateContent((double progress0To1, string processingState) =>
						{
							progressStatus.Progress0To1 = progress0To1;
							progressStatus.Status = processingState;
							progressReporter.Report(progressStatus);
						});
					});

					if (object3D != null)
					{
						Scene.Children.Modify(list => list.Add(object3D));

						PlatingHelper.MoveToOpenPositionRelativeGroup(object3D, this.Scene.Children);
					}
				}
			}
		}

		internal void MakeLowestFaceFlat(IObject3D objectToLayFlatGroup)
		{
			bool firstVertex = true;

			IObject3D objectToLayFlat = objectToLayFlatGroup;

			IVertex lowestVertex = null;
			Vector3 lowestVertexPosition = Vector3.Zero;
			IObject3D itemToLayFlat = null;

			// Process each child, checking for the lowest vertex
			var objectsToCheck = objectToLayFlat.VisibleMeshes();
			foreach (var itemToCheck in objectsToCheck)
			{
				// find the lowest point on the model
				for (int testIndex = 0; testIndex < itemToCheck.Mesh.Vertices.Count; testIndex++)
				{
					var vertex = itemToCheck.Mesh.Vertices[testIndex];
					Vector3 vertexPosition = Vector3.Transform(vertex.Position, itemToCheck.WorldMatrix());
					if(firstVertex)
					{
						lowestVertex = itemToCheck.Mesh.Vertices[testIndex];
						lowestVertexPosition = vertexPosition;
						itemToLayFlat = itemToCheck;
						firstVertex = false;
					}
					else if (vertexPosition.Z < lowestVertexPosition.Z)
					{
						lowestVertex = itemToCheck.Mesh.Vertices[testIndex];
						lowestVertexPosition = vertexPosition;
						itemToLayFlat = itemToCheck;
					}
				}
			}

			if(lowestVertex == null)
			{
				// didn't find any selected mesh
				return;
			}

			Face faceToLayFlat = null;
			double lowestAngleOfAnyFace = double.MaxValue;
			// Check all the faces that are connected to the lowest point to find out which one to lay flat.
			foreach (Face face in lowestVertex.ConnectedFaces())
			{
				double biggestAngleToFaceVertex = double.MinValue;
				foreach (IVertex faceVertex in face.Vertices())
				{
					if (faceVertex != lowestVertex)
					{
						Vector3 faceVertexPosition = Vector3.Transform(faceVertex.Position, itemToLayFlat.WorldMatrix());
						Vector3 pointRelLowest = faceVertexPosition - lowestVertexPosition;
						double xLeg = new Vector2(pointRelLowest.X, pointRelLowest.Y).Length;
						double yLeg = pointRelLowest.Z;
						double angle = Math.Atan2(yLeg, xLeg);
						if (angle > biggestAngleToFaceVertex)
						{
							biggestAngleToFaceVertex = angle;
						}
					}
				}
				if (biggestAngleToFaceVertex < lowestAngleOfAnyFace)
				{
					lowestAngleOfAnyFace = biggestAngleToFaceVertex;
					faceToLayFlat = face;
				}
			}

			double maxDistFromLowestZ = 0;
			List<Vector3> faceVertices = new List<Vector3>();
			foreach (IVertex vertex in faceToLayFlat.Vertices())
			{
				Vector3 vertexPosition = Vector3.Transform(vertex.Position, itemToLayFlat.WorldMatrix());
				faceVertices.Add(vertexPosition);
				maxDistFromLowestZ = Math.Max(maxDistFromLowestZ, vertexPosition.Z - lowestVertexPosition.Z);
			}

			if (maxDistFromLowestZ > .001)
			{
				Vector3 xPositive = (faceVertices[1] - faceVertices[0]).GetNormal();
				Vector3 yPositive = (faceVertices[2] - faceVertices[0]).GetNormal();
				Vector3 planeNormal = Vector3.Cross(xPositive, yPositive).GetNormal();

				// this code takes the minimum rotation required and looks much better.
				Quaternion rotation = new Quaternion(planeNormal, new Vector3(0, 0, -1));
				Matrix4X4 partLevelMatrix = Matrix4X4.CreateRotation(rotation);

				// rotate it
				objectToLayFlatGroup.Matrix = PlatingHelper.ApplyAtCenter(objectToLayFlatGroup, partLevelMatrix);

				Scene.Invalidate();
				Invalidate();
			}

			PlatingHelper.PlaceOnBed(objectToLayFlatGroup);
		}

		public static Regex fileNameNumberMatch = new Regex("\\(\\d+\\)", RegexOptions.Compiled);

		private FlowLayoutWidget editorPanel;

		private SelectedObjectPanel selectedObjectPanel;

		internal GuiWidget selectedObjectContainer;

		public Task SaveChanges(IProgress<ProgressStatus> progress, CancellationToken cancellationToken)
		{
			var progressStatus = new ProgressStatus()
			{
				Status = "Saving Changes"
			};

			progress.Report(progressStatus);

			sceneContext.Save((progress0to1, status) =>
			{
				progressStatus.Status = status;
				progressStatus.Progress0To1 = progress0to1;
				progress.Report(progressStatus);
			});

			return Task.CompletedTask;
		}

		private void OpenSaveAsWindow()
		{
			DialogWindow.Show(
				new SaveAsPage(
					async (newName, destinationContainer) =>
					{
						// Save the scene to disk
						await ApplicationController.Instance.Tasks.Execute(this.SaveChanges);

						// Save to the destination provider
						if (destinationContainer != null)
						{
							// save this part to correct library provider
							if (destinationContainer is ILibraryWritableContainer writableContainer)
							{
								writableContainer.Add(new[]
								{
									new FileSystemFileItem(sceneContext.EditContext.PartFilePath)
									{
										Name = newName
									}
								});

								destinationContainer.Dispose();
							}
						}
					}));
		}

		public Vector2 DragSelectionStartPosition { get; private set; }
		public bool DragSelectionInProgress { get; private set; }
		public Vector2 DragSelectionEndPosition { get; private set; }

		internal async void SwitchStateToEditing()
		{
			viewControls3D.ActiveButton = ViewControls3DButtons.PartSelect;

			if (Scene.HasChildren())
			{
				// This should be very fast (only building up a trace data for non-meshes, mesh should happen as background task).
				await Task.Run(() =>
				{
					Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

					// Force trace data generation
					foreach (var object3D in Scene.Children)
					{
						object3D.TraceData();
					}
				});

				if (this.HasBeenClosed)
				{
					return;
				}

				Scene.SelectFirstChild();
			}

			this.Invalidate();
			Scene.Invalidate();

			viewControls3D.ActiveButton = ViewControls3DButtons.PartSelect;
		}

		internal GuiWidget ShowOverflowMenu()
		{
			var popupMenu = new PopupMenu(theme);

			var meshViewer = meshViewerWidget;

			popupMenu.CreateBoolMenuItem(
				"Show Print Bed".Localize(),
				() => sceneContext.RendererOptions.RenderBed,
				(value) =>
				{
					meshViewer.RenderBed = value;
					sceneContext.RendererOptions.RenderBed = value;
				});

			if (sceneContext.BuildHeight > 0)
			{
				popupMenu.CreateBoolMenuItem(
					"Show Print Area".Localize(),
					() => meshViewer.RenderBuildVolume,
					(value) => meshViewer.RenderBuildVolume = value);
			}

			popupMenu.CreateHorizontalLine();

			// TODO: This should be moved to the MeshViewerWidget constructor or initializer calls
			string renderTypeString = UserSettings.Instance.get(UserSettingsKey.defaultRenderSetting);
			if (renderTypeString == null)
			{
				renderTypeString = (UserSettings.Instance.IsTouchScreen) ? "Shaded" : "Outlines";
				UserSettings.Instance.set(UserSettingsKey.defaultRenderSetting, renderTypeString);
			}

			RenderTypes renderType;
			bool canParse = Enum.TryParse(renderTypeString, out renderType);
			if (canParse)
			{
				meshViewerWidget.RenderType = renderType;
			}

			AddRadioButton("Shaded".Localize(), RenderTypes.Shaded, popupMenu);
			AddRadioButton("Outlines".Localize(), RenderTypes.Outlines, popupMenu);
			AddRadioButton("Polygons".Localize(), RenderTypes.Polygons, popupMenu);
			AddRadioButton("Materials Option".Localize(), RenderTypes.Materials, popupMenu);
			AddRadioButton("Overhang".Localize(), RenderTypes.Overhang, popupMenu, () =>
			{
				meshViewerWidget.RenderType = RenderTypes.Overhang;

				UserSettings.Instance.set("defaultRenderSetting", meshViewerWidget.RenderType.ToString());

				// Loop over all visible meshes, changing the face color depending on the face normal Z value
				foreach (var meshRenderData in this.Scene.VisibleMeshes())
				{
					meshRenderData.Mesh.MarkAsChanged();

					// change the color to be the right thing
					GLMeshTrianglePlugin.Get(
						meshRenderData.Mesh, 
						(normal) =>
						{
							normal = Vector3.TransformVector(normal, meshRenderData.WorldMatrix()).GetNormal();

							double startColor = 223.0 / 360.0;
							double endColor = 5.0 / 360.0;
							double delta = endColor - startColor;

							var color = ColorF.FromHSL(startColor, .99, .49).ToColor();
							if (normal.Z < 0)
							{
								color = ColorF.FromHSL(startColor - delta * normal.Z, .99, .49).ToColor();
							}

							return color;
						});
				}
			});

			popupMenu.CreateHorizontalLine();

			popupMenu.AddChild(new GridOptionsPanel(this.InteractionLayer));

			return popupMenu;
		}

		private void AddRadioButton(string label, RenderTypes renderTypes, PopupMenu popupMenu, Action action = null)
		{
			var radioButton = new RadioButton(label, textColor: Color.Black)
			{
				Checked = (meshViewerWidget.RenderType == renderTypes),
				HAnchor = HAnchor.MaxFitOrStretch,
				Margin = 0
			};
			radioButton.CheckedStateChanged += (s, e) =>
			{
				if (radioButton.Checked)
				{
					if (action == null)
					{
						meshViewerWidget.RenderType = renderTypes;
						UserSettings.Instance.set(UserSettingsKey.defaultRenderSetting, meshViewerWidget.RenderType.ToString());
					}
					else
					{
						action.Invoke();
					}
				}
			};

			popupMenu.CreateMenuItem(radioButton, $"{label}-Menu");
		}

		protected bool autoRotating = false;
		protected bool allowAutoRotate = false;

		public MeshViewerWidget meshViewerWidget;

		public InteractiveScene Scene { get; }

		protected ViewControls3D viewControls3D { get; }

		public MeshSelectInfo CurrentSelectInfo { get; } = new MeshSelectInfo();

		protected IObject3D FindHitObject3D(Vector2 screenPosition, ref IntersectInfo intersectionInfo)
		{
			Vector2 meshViewerWidgetScreenPosition = meshViewerWidget.TransformFromParentSpace(this, screenPosition);
			Ray ray = this.World.GetRayForLocalBounds(meshViewerWidgetScreenPosition);

			intersectionInfo = Scene.TraceData().GetClosestIntersection(ray);
			if (intersectionInfo != null)
			{
				foreach (Object3D object3D in Scene.Children)
				{
					if (object3D.TraceData().Contains(intersectionInfo.HitPosition))
					{
						CurrentSelectInfo.PlaneDownHitPos = intersectionInfo.HitPosition;
						CurrentSelectInfo.LastMoveDelta = new Vector3();
						return object3D;
					}
				}
			}

			return null;
		}
	}

	public enum HitQuadrant { LB, LT, RB, RT }
	public class MeshSelectInfo
	{
		public HitQuadrant HitQuadrant;
		public bool DownOnPart;
		public PlaneShape HitPlane;
		public Vector3 LastMoveDelta;
		public Vector3 PlaneDownHitPos;
	}
}
