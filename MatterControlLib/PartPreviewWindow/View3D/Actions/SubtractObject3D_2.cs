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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.DesignTools;
using MatterHackers.MatterControl.DesignTools.Operations;
using MatterHackers.RenderOpenGl;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.PartPreviewWindow.View3D
{
	[ShowUpdateButton]
	public class SubtractObject3D_2 : OperationSourceContainerObject3D, ISelectableChildContainer, IEditorDraw
	{
		public SubtractObject3D_2()
		{
			Name = "Subtract";
		}

		public SelectedChildren SelectedChildren { get; set; } = new SelectedChildren();

		public void DrawEditor(InteractionLayer layer, List<Object3DView> transparentMeshes, DrawEventArgs e, ref bool suppressNormalDraw)
		{
			if (layer.Scene.SelectedItem != null
				&& layer.Scene.SelectedItem == this)
			{
				suppressNormalDraw = true;

				var removeObjects = this.SourceContainer.VisibleMeshes()
					.Where((i) => SelectedChildren.Contains(i.Name)).ToList();
				var keepObjects = this.SourceContainer.VisibleMeshes()
					.Where((i) => !SelectedChildren.Contains(i.Name)).ToList();

				foreach (var item in removeObjects)
				{
					transparentMeshes.Add(new Object3DView(item, new Color(item.WorldColor(SourceContainer), 128)));
				}

				foreach (var item in keepObjects)
				{
					var subtractChild = this.Children.Where(i => i.Name == item.Name).FirstOrDefault();
					if (subtractChild != null)
					{
						GLHelper.Render(subtractChild.Mesh,
							subtractChild.Color,
							subtractChild.WorldMatrix(),
							RenderTypes.Outlines,
							subtractChild.WorldMatrix() * layer.World.ModelviewMatrix);
					}
					else
					{
						GLHelper.Render(item.Mesh,
							item.WorldColor(SourceContainer),
							item.WorldMatrix(),
							RenderTypes.Outlines,
							item.WorldMatrix() * layer.World.ModelviewMatrix);
					}
				}
			}
		}

		public override async void OnInvalidate(InvalidateArgs invalidateType)
		{
			if ((invalidateType.InvalidateType.HasFlag(InvalidateType.Children)
				|| invalidateType.InvalidateType.HasFlag(InvalidateType.Matrix)
				|| invalidateType.InvalidateType.HasFlag(InvalidateType.Mesh))
				&& invalidateType.Source != this
				&& !RebuildLocked)
			{
				await Rebuild();
			}
			else if (invalidateType.InvalidateType.HasFlag(InvalidateType.Properties)
				&& invalidateType.Source == this)
			{
				await Rebuild();
			}
			else
			{
				base.OnInvalidate(invalidateType);
			}
		}

		public override Task Rebuild()
		{
			this.DebugDepth("Rebuild");

			var rebuildLocks = this.RebuilLockAll();

			return ApplicationController.Instance.Tasks.Execute(
				"Subtract".Localize(),
				null,
				(reporter, cancellationToken) =>
				{
					var progressStatus = new ProgressStatus();
					reporter.Report(progressStatus);

					try
					{
						Subtract(cancellationToken, reporter);
					}
					catch
					{
					}

					rebuildLocks.Dispose();
					Parent?.Invalidate(new InvalidateArgs(this, InvalidateType.Children));
					return Task.CompletedTask;
				});
		}

		public void Subtract()
		{
			Subtract(CancellationToken.None, null);
		}

		private void Subtract(CancellationToken cancellationToken, IProgress<ProgressStatus> reporter)
		{
			SourceContainer.Visible = true;
			RemoveAllButSource();

			var visibleMeshes = SourceContainer.VisibleMeshes();
			if (visibleMeshes.Count() < 2)
			{
				if (visibleMeshes.Count() == 1)
				{
					var newMesh = new Object3D();
					newMesh.CopyProperties(visibleMeshes.First(), Object3DPropertyFlags.All);
					newMesh.Mesh = visibleMeshes.First().Mesh;
					this.Children.Add(newMesh);
					SourceContainer.Visible = false;
				}
				return;
			}

			CleanUpSelectedChildrenNames(this);

			var removeObjects = this.SourceContainer.VisibleMeshes()
				.Where((i) => SelectedChildren.Contains(i.Name)).ToList();
			var keepObjects = this.SourceContainer.VisibleMeshes()
				.Where((i) => !SelectedChildren.Contains(i.Name)).ToList();

			if (removeObjects.Any()
				&& keepObjects.Any())
			{
				var totalOperations = removeObjects.Count * keepObjects.Count;
				double amountPerOperation = 1.0 / totalOperations;
				double percentCompleted = 0;

				ProgressStatus progressStatus = new ProgressStatus();
				progressStatus.Status = "Do CSG";
				foreach (var keep in keepObjects)
				{
					var resultsMesh = keep.Mesh;
					var keepWorldMatrix = keep.WorldMatrix(SourceContainer);

					foreach (var remove in removeObjects)
					{
						resultsMesh = BooleanProcessing.Do(resultsMesh, keepWorldMatrix,
							remove.Mesh, remove.WorldMatrix(SourceContainer),
							1, reporter, amountPerOperation, percentCompleted, progressStatus, cancellationToken);

						// after the first time we get a result the results mesh is in the right coordinate space
						keepWorldMatrix = Matrix4X4.Identity;

						// report our progress
						percentCompleted += amountPerOperation;
						progressStatus.Progress0To1 = percentCompleted;
						reporter?.Report(progressStatus);
					}

					// store our results mesh
					var resultsItem = new Object3D()
					{
						Mesh = resultsMesh,
						Visible = false
					};
					// copy all the properties but the matrix
					resultsItem.CopyProperties(keep, Object3DPropertyFlags.All & (~(Object3DPropertyFlags.Matrix | Object3DPropertyFlags.Visible)));
					// and add it to this
					this.Children.Add(resultsItem);
				}

				bool first = true;
				foreach (var child in Children)
				{
					if (first)
					{
						// hid the source item
						child.Visible = false;
						first = false;
					}
					else
					{
						child.Visible = true;
					}
				}
			}
		}

		public static void CleanUpSelectedChildrenNames(OperationSourceContainerObject3D item)
		{
			if (item is ISelectableChildContainer selectableChildContainer)
			{
				var allVisibleNames = item.SourceContainer.VisibleMeshes().Select(i => i.Name);
				// remove any names from SelectedChildren that are not in visible meshes
				foreach (var name in selectableChildContainer.SelectedChildren.ToArray())
				{
					if (!allVisibleNames.Contains(name))
					{
						selectableChildContainer.SelectedChildren.Remove(name);
					}
				}
			}
		}
	}
}