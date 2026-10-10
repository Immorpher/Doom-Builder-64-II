
#region ================== Copyright (c) 2007 Pascal vd Heiden

/*
 * Copyright (c) 2007 Pascal vd Heiden, www.codeimp.com
 * This program is released under GNU General Public License
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 */

#endregion

#region ================== Namespaces

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using System.IO;
using System.Reflection;
using System.Drawing;
using System.ComponentModel;
using SlimDX;
using CodeImp.DoomBuilder.Geometry;
using SlimDX.Direct3D9;
using CodeImp.DoomBuilder.Data;
using CodeImp.DoomBuilder.VisualModes;
using CodeImp.DoomBuilder.Map;
using CodeImp.DoomBuilder.IO;   // villsa

#endregion

namespace CodeImp.DoomBuilder.Rendering
{
	internal sealed class Renderer3D : Renderer, IRenderer3D
	{
		#region ================== Constants

		private const int RENDER_PASSES = 4;
		private const float PROJ_NEAR_PLANE = 1f;
		private const float CROSSHAIR_SCALE = 0.06f;
		private const float FOG_RANGE = 0.9f;
		
		#endregion

		#region ================== Variables

		// Matrices
		private Matrix projection;
		private Matrix view3d;
		private Matrix billboard;
		private Matrix worldviewproj;
		private Matrix view2d;
		private Matrix world;
		private Vector3D cameraposition;
		private int shaderpass;
		
		// Options

        private bool showlightonly; // villsa
		
		// Window size
		private Size windowsize;
		
		// Frustum
		private ProjectedFrustum2D frustum;
		
		// Thing cage
		private VertexBuffer thingcage;
		private bool renderthingcages;

        // villsa 9/15/11 (builder64) thing arrows
        private VertexBuffer thingarrow;
		
		// Crosshair
		private FlatVertex[] crosshairverts;
		private bool crosshairbusy;

		// Highlighting
		private IVisualPickable highlighted;
		private float highlightglow;
		private float highlightglowinv;
		private ColorImage highlightimage;
		private ColorImage selectionimage;
		private bool showselection;
		private bool showhighlight;
		
		// Geometry to be rendered.
		// Each Dictionary in the array is a render pass.
		// Each BinaryHeap in the Dictionary contains all geometry that needs
		// to be rendered with the associated ImageData.
		// The BinaryHeap sorts the geometry by sector to minimize stream switchs.
		private Dictionary<ImageData, BinaryHeap<VisualGeometry>>[] geometry;

		// Things to be rendered.
		// Each Dictionary in the array is a render pass.
		// Each VisualThing is inserted in the Dictionary by their texture image.
		private Dictionary<ImageData, List<VisualThing>>[] things;

		// Things to be rendered, sorted by distance from camera
		private BinaryHeap<VisualThing> thingsbydistance;

		// Collections that are reused from frame to frame, to avoid creating new ones every frame
		private Stack<BinaryHeap<VisualGeometry>> geometryheappool = new Stack<BinaryHeap<VisualGeometry>>();
		private Stack<List<VisualThing>> thinglistpool = new Stack<List<VisualThing>>();

		#endregion

		#region ================== Properties

		public ProjectedFrustum2D Frustum2D { get { return frustum; } }
		public bool DrawThingCages { get { return renderthingcages; } set { renderthingcages = value; } }
		public new bool FullBrightness { get { return Renderer.FullBrightness; } set { Renderer.FullBrightness = value; } }
		public bool ShowSelection { get { return showselection; } set { showselection = value; } }
		public bool ShowHighlight { get { return showhighlight; } set { showhighlight = value; } }
        public bool ShowLightOnly { get { return showlightonly; } set { showlightonly = value; } }  // villsa
		
		#endregion

		#region ================== Constructor / Disposer

		// Constructor
		internal Renderer3D(D3DDevice graphics) : base(graphics)
		{
			// Initialize
			CreateProjection();
			CreateMatrices2D();
			SetupThingCage();
            SetupThingArrow();  // villsa
			SetupTextures();
			renderthingcages = true;
			showselection = true;
			showhighlight = true;
			
			// Dummy frustum
			frustum = new ProjectedFrustum2D(new Vector2D(), 0.0f, 0.0f, PROJ_NEAR_PLANE,
				General.Settings.ViewDistance, Angle2D.DegToRad((float)General.Settings.VisualFOV));

			// We have no destructor
			GC.SuppressFinalize(this);
		}

		// Disposer
		internal override void Dispose()
		{
			// Not already disposed?
			if(!isdisposed)
			{
				// Clean up
				if(thingcage != null) thingcage.Dispose();
                if (thingarrow != null) thingarrow.Dispose();   // villsa 9/15/11
				if(selectionimage != null) selectionimage.Dispose();
				if(highlightimage != null) highlightimage.Dispose();
				thingcage = null;
                thingarrow = null;  // villsa 9/15/11
				selectionimage = null;
				highlightimage = null;
				
				// Done
				base.Dispose();
			}
		}

		#endregion

		#region ================== Management

		// This is called before a device is reset
		// (when resized or display adapter was changed)
		public override void UnloadResource()
		{
			crosshairverts = null;
			if(thingcage != null) thingcage.Dispose();
            if (thingarrow != null) thingarrow.Dispose();   // villsa 9/15/11
			if(selectionimage != null) selectionimage.Dispose();
			if(highlightimage != null) highlightimage.Dispose();
			thingcage = null;
            thingarrow = null;  // villsa 9/15/11
			selectionimage = null;
			highlightimage = null;
		}
		
		// This is called resets when the device is reset
		// (when resized or display adapter was changed)
		public override void ReloadResource()
		{
			CreateMatrices2D();
			SetupThingCage();
            SetupThingArrow();  // villsa
			SetupTextures();
		}

		// This makes screen vertices for display
		private void CreateCrosshairVerts(Size texturesize)
		{
			// Determine coordinates
			float width = (float)windowsize.Width;
			float height = (float)windowsize.Height;
			float size = (float)height * CROSSHAIR_SCALE;
			RectangleF rect = new RectangleF((width - size) / 2, (height - size) / 2, size, size);
			
			// Make vertices
			crosshairverts = new FlatVertex[4];
			crosshairverts[0].x = rect.Left;
			crosshairverts[0].y = rect.Top;
			crosshairverts[0].c = -1;
			crosshairverts[0].u = 1f / texturesize.Width;
			crosshairverts[0].v = 1f / texturesize.Height;
			crosshairverts[1].x = rect.Right;
			crosshairverts[1].y = rect.Top;
			crosshairverts[1].c = -1;
			crosshairverts[1].u = 1f - 1f / texturesize.Width;
			crosshairverts[1].v = 1f / texturesize.Height;
			crosshairverts[2].x = rect.Left;
			crosshairverts[2].y = rect.Bottom;
			crosshairverts[2].c = -1;
			crosshairverts[2].u = 1f / texturesize.Width;
			crosshairverts[2].v = 1f - 1f / texturesize.Height;
			crosshairverts[3].x = rect.Right;
			crosshairverts[3].y = rect.Bottom;
			crosshairverts[3].c = -1;
			crosshairverts[3].u = 1f - 1f / texturesize.Width;
			crosshairverts[3].v = 1f - 1f / texturesize.Height;
		}
		
		#endregion

		#region ================== Resources

		// This loads the textures for highlight and selection if we need them
		private void SetupTextures()
		{
			if(!graphics.Shaders.Enabled)
			{
				highlightimage = new ColorImage(General.Colors.Highlight, 32, 32);
				highlightimage.LoadImage();
				highlightimage.CreateTexture();
				
				selectionimage = new ColorImage(General.Colors.Selection, 32, 32);
				selectionimage.LoadImage();
				selectionimage.CreateTexture();
			}
		}

        // villsa 9/15/11
        private void SetupThingArrow()
        {
            const int totalvertices = 6;
            WorldVertex[] tv = new WorldVertex[totalvertices];

            tv[0] = new WorldVertex(0.0f, 0.0f, 0.5f);
            tv[1] = new WorldVertex(0.0f, 1.35f, 0.5f);
            tv[2] = new WorldVertex(0.0f, 1.35f, 0.5f);
            tv[3] = new WorldVertex(0.175f, 1.1f, 0.5f);
            tv[4] = new WorldVertex(0.0f, 1.35f, 0.5f);
            tv[5] = new WorldVertex(-0.175f, 1.1f, 0.5f);

            // Create vertexbuffer
            thingarrow = new VertexBuffer(General.Map.Graphics.Device, WorldVertex.Stride * totalvertices,
                                         Usage.WriteOnly | Usage.Dynamic, VertexFormat.None, Pool.Default);
            DataStream bufferstream = thingarrow.Lock(0, WorldVertex.Stride * totalvertices, LockFlags.Discard);
            bufferstream.WriteRange<WorldVertex>(tv);
            thingarrow.Unlock();
            bufferstream.Dispose();
        }
		
		// This sets up the thing cage
		private void SetupThingCage()
		{
			const int totalvertices = 36;
			WorldVertex[] tv = new WorldVertex[totalvertices];
			float x0 = -1.0f;
			float x1 = 1.0f;
			float y0 = -1.0f;
			float y1 = 1.0f;
			float z0 = 0.0f;
			float z1 = 1.0f;
			float u0 = 0.0f;
			float u1 = 1.0f;
			float v0 = 0.0f;
			float v1 = 1.0f;
			int c = -1;
			
			// Front
			tv[0] = new WorldVertex(x0, y0, z0, c, u0, v0);
			tv[1] = new WorldVertex(x0, y0, z1, c, u0, v1);
			tv[2] = new WorldVertex(x1, y0, z0, c, u1, v0);
			tv[3] = new WorldVertex(x1, y0, z0, c, u1, v0);
			tv[4] = new WorldVertex(x0, y0, z1, c, u0, v1);
			tv[5] = new WorldVertex(x1, y0, z1, c, u1, v1);

			// Right
			tv[6] = new WorldVertex(x1, y0, z0, c, u0, v0);
			tv[7] = new WorldVertex(x1, y0, z1, c, u0, v1);
			tv[8] = new WorldVertex(x1, y1, z0, c, u1, v0);
			tv[9] = new WorldVertex(x1, y1, z0, c, u1, v0);
			tv[10] = new WorldVertex(x1, y0, z1, c, u0, v1);
			tv[11] = new WorldVertex(x1, y1, z1, c, u1, v1);

			// Back
			tv[12] = new WorldVertex(x1, y1, z0, c, u0, v0);
			tv[13] = new WorldVertex(x1, y1, z1, c, u0, v1);
			tv[14] = new WorldVertex(x0, y1, z0, c, u1, v0);
			tv[15] = new WorldVertex(x0, y1, z0, c, u1, v0);
			tv[16] = new WorldVertex(x1, y1, z1, c, u0, v1);
			tv[17] = new WorldVertex(x0, y1, z1, c, u1, v1);

			// Left
			tv[18] = new WorldVertex(x0, y1, z0, c, u0, v1);
			tv[19] = new WorldVertex(x0, y1, z1, c, u0, v0);
			tv[20] = new WorldVertex(x0, y0, z1, c, u1, v0);
			tv[21] = new WorldVertex(x0, y1, z0, c, u1, v0);
			tv[22] = new WorldVertex(x0, y0, z1, c, u0, v1);
			tv[23] = new WorldVertex(x0, y0, z0, c, u1, v1);

			// Top
			tv[24] = new WorldVertex(x0, y0, z1, c, u0, v0);
			tv[25] = new WorldVertex(x0, y1, z1, c, u0, v1);
			tv[26] = new WorldVertex(x1, y0, z1, c, u1, v0);
			tv[27] = new WorldVertex(x1, y0, z1, c, u1, v0);
			tv[28] = new WorldVertex(x0, y1, z1, c, u0, v1);
			tv[29] = new WorldVertex(x1, y1, z1, c, u1, v1);

			// Bottom
			tv[30] = new WorldVertex(x1, y0, z0, c, u1, v0);
			tv[31] = new WorldVertex(x0, y1, z0, c, u0, v1);
			tv[32] = new WorldVertex(x0, y0, z0, c, u0, v0);
			tv[33] = new WorldVertex(x1, y0, z0, c, u1, v0);
			tv[34] = new WorldVertex(x1, y1, z0, c, u1, v1);
			tv[35] = new WorldVertex(x0, y1, z0, c, u0, v1);

			// Create vertexbuffer
			thingcage = new VertexBuffer(General.Map.Graphics.Device, WorldVertex.Stride * totalvertices,
										 Usage.WriteOnly | Usage.Dynamic, VertexFormat.None, Pool.Default);
			DataStream bufferstream = thingcage.Lock(0, WorldVertex.Stride * totalvertices, LockFlags.Discard);
			bufferstream.WriteRange<WorldVertex>(tv);
			thingcage.Unlock();
			bufferstream.Dispose();
		}

		#endregion
		
		#region ================== Presentation

		// This creates the projection
		internal void CreateProjection()
		{
			// Calculate aspect
			float aspect = (float)General.Map.Graphics.RenderTarget.ClientSize.Width /
						   (float)General.Map.Graphics.RenderTarget.ClientSize.Height;
			
			// The DirectX PerspectiveFovRH matrix method calculates the scaling in X and Y as follows:
			// yscale = 1 / tan(fovY / 2)
			// xscale = yscale / aspect
			// The fov specified in the method is the FOV over Y, but we want the user to specify the FOV
			// over X, so calculate what it would be over Y first;
			float fov = Angle2D.DegToRad((float)General.Settings.VisualFOV);
			float reversefov = 1.0f / (float)Math.Tan(fov / 2.0f);
			float reversefovy = reversefov * aspect;
			float fovy = (float)Math.Atan(1.0f / reversefovy) * 2.0f;
			
			// Make the projection matrix
			projection = Matrix.PerspectiveFovRH(fovy, aspect, PROJ_NEAR_PLANE, General.Settings.ViewDistance);

			// Apply matrices
			ApplyMatrices3D();
		}
		
		// This creates matrices for a camera view
		public void PositionAndLookAt(Vector3D pos, Vector3D lookat)
		{
			Vector3D delta;
			float anglexy, anglez;
			
			// Calculate delta vector
			cameraposition = pos;
			delta = lookat - pos;
			anglexy = delta.GetAngleXY();
			anglez = delta.GetAngleZ();

			// Create frustum
			frustum = new ProjectedFrustum2D(pos, anglexy, anglez, PROJ_NEAR_PLANE,
				General.Settings.ViewDistance, Angle2D.DegToRad((float)General.Settings.VisualFOV));
			
			// Make the view matrix
			view3d = Matrix.LookAtRH(D3DDevice.V3(pos), D3DDevice.V3(lookat), new Vector3(0f, 0f, 1f));
			
			// Make the billboard matrix
			billboard = Matrix.RotationZ(anglexy + Angle2D.PI);
		}
		
		// This creates 2D view matrix
		private void CreateMatrices2D()
		{
			windowsize = graphics.RenderTarget.ClientSize;
			Matrix scaling = Matrix.Scaling((1f / (float)windowsize.Width) * 2f, (1f / (float)windowsize.Height) * -2f, 1f);
			Matrix translate = Matrix.Translation(-(float)windowsize.Width * 0.5f, -(float)windowsize.Height * 0.5f, 0f);
			view2d = Matrix.Multiply(translate, scaling);
		}
		
		// This applies the matrices
		private void ApplyMatrices3D()
		{
			worldviewproj = world * view3d * projection;
			graphics.Shaders.World3D.WorldViewProj = worldviewproj;
			graphics.Device.SetTransform(TransformState.World, world);
			graphics.Device.SetTransform(TransformState.Projection, projection);
			graphics.Device.SetTransform(TransformState.View, view3d);
		}

		// This sets the appropriate view matrix
		public void ApplyMatrices2D()
		{
			graphics.Device.SetTransform(TransformState.World, world);
			graphics.Device.SetTransform(TransformState.Projection, Matrix.Identity);
			graphics.Device.SetTransform(TransformState.View, view2d);
		}
		
		#endregion

		#region ================== Start / Finish

		// This starts rendering
		public bool Start()
		{
			// Create texture
			if(General.Map.Data.ThingBox.Texture == null)
				General.Map.Data.ThingBox.CreateTexture();
			
			// Start drawing
			if(graphics.StartRendering(true, General.Colors.Background.ToColorValue(), graphics.BackBuffer, graphics.DepthBuffer))
			{
				// Beginning renderstates
				graphics.Device.SetRenderState(RenderState.CullMode, Cull.None);
				graphics.Device.SetRenderState(RenderState.ZEnable, false);
				graphics.Device.SetRenderState(RenderState.AlphaBlendEnable, false);
				graphics.Device.SetRenderState(RenderState.AlphaTestEnable, false);
				graphics.Device.SetRenderState(RenderState.SourceBlend, Blend.SourceAlpha);
				graphics.Device.SetRenderState(RenderState.DestinationBlend, Blend.InverseSourceAlpha);
				graphics.Device.SetRenderState(RenderState.FogEnable, false);
				graphics.Device.SetRenderState(RenderState.FogDensity, 1.0f);
				graphics.Device.SetRenderState(RenderState.FogColor, General.Colors.Background.ToInt());
				graphics.Device.SetRenderState(RenderState.FogStart, General.Settings.ViewDistance * FOG_RANGE);
				graphics.Device.SetRenderState(RenderState.FogEnd, General.Settings.ViewDistance);
				graphics.Device.SetRenderState(RenderState.FogTableMode, FogMode.Linear);
				graphics.Device.SetRenderState(RenderState.RangeFogEnable, false);
				graphics.Device.SetRenderState(RenderState.TextureFactor, -1);
				graphics.Shaders.World3D.SetModulateColor(-1);
				graphics.Shaders.World3D.SetHighlightColor(0);
				graphics.Shaders.World3D.SetGlowLevel(0.0f);
				graphics.Shaders.World3D.SetUVOffset(0.0f, 0.0f);

				// Texture addressing
				graphics.Device.SetSamplerState(0, SamplerState.AddressU, TextureAddress.Wrap);
				graphics.Device.SetSamplerState(0, SamplerState.AddressV, TextureAddress.Wrap);
				graphics.Device.SetSamplerState(0, SamplerState.AddressW, TextureAddress.Wrap);

				// Matrices
				world = Matrix.Identity;
				ApplyMatrices3D();

				// Highlight
				if(General.Settings.AnimateVisualSelection)
				{
					double time = General.stopwatch.Elapsed.TotalMilliseconds;
					highlightglow = (float)Math.Sin(time / 100.0f) * 0.1f + 0.4f;
					highlightglowinv = -(float)Math.Sin(time / 100.0f) * 0.1f + 0.4f;
				}
				else
				{
					highlightglow = 0.4f;
					highlightglowinv = 0.3f;
				}
				
				// Determine shader pass to use
				if(Renderer.FullBrightness) shaderpass = 1; else shaderpass = 0;

				// Create crosshair vertices
				if(crosshairverts == null)
					CreateCrosshairVerts(new Size(General.Map.Data.Crosshair3D.Width, General.Map.Data.Crosshair3D.Height));
				
				// Ready
				return true;
			}
			else
			{
				// Can't render now
				return false;
			}
		}
		
		// This begins rendering world geometry
		public void StartGeometry()
		{
			// Advance the Doom 64 glowing sector lights (runs at the game's 30 tics per second)
			SectorGlow.Update();

			// Advance the Doom 64 texture scrolling (also runs at the game's 30 tics per second)
			TextureScroll.Update();

			// Advance the Doom 64 texture animations (ANIMDEFS, also 30 tics per second)
			if(General.Map.Data.Animations != null) General.Map.Data.Animations.Update();

			// Make collections (once, they are reused for every frame)
			if(geometry == null)
			{
				geometry = new Dictionary<ImageData, BinaryHeap<VisualGeometry>>[RENDER_PASSES];
				things = new Dictionary<ImageData, List<VisualThing>>[RENDER_PASSES];
				thingsbydistance = new BinaryHeap<VisualThing>();
				for(int i = 0; i < RENDER_PASSES; i++)
				{
					geometry[i] = new Dictionary<ImageData, BinaryHeap<VisualGeometry>>();
					things[i] = new Dictionary<ImageData, List<VisualThing>>();
				}
			}
			
			// Make sure nothing is left over from a previous frame that was not finished
			RecycleGeometry();
		}

		// This empties the collected geometry and things and keeps the collections for reuse
		private void RecycleGeometry()
		{
			if(geometry == null) return;
			
			for(int i = 0; i < RENDER_PASSES; i++)
			{
				foreach(BinaryHeap<VisualGeometry> heap in geometry[i].Values)
				{
					heap.Clear();
					geometryheappool.Push(heap);
				}
				geometry[i].Clear();

				foreach(List<VisualThing> list in things[i].Values)
				{
					list.Clear();
					thinglistpool.Push(list);
				}
				things[i].Clear();
			}
			thingsbydistance.Clear();
		}

		// This ends rendering world geometry
		public void FinishGeometry()
		{
			// Initial renderstates
			graphics.Device.SetRenderState(RenderState.CullMode, Cull.Counterclockwise);
			graphics.Device.SetRenderState(RenderState.ZEnable, true);
			graphics.Device.SetRenderState(RenderState.ZWriteEnable, true);
			graphics.Device.SetRenderState(RenderState.AlphaBlendEnable, false);
			graphics.Device.SetRenderState(RenderState.AlphaTestEnable, false);
			graphics.Device.SetRenderState(RenderState.TextureFactor, -1);
			graphics.Shaders.World3D.Begin();

			// SOLID PASS
			world = Matrix.Identity;
			ApplyMatrices3D();
			RenderSinglePass((int)RenderPass.Solid);

			// MASK PASS
			world = Matrix.Identity;
			ApplyMatrices3D();
			graphics.Device.SetRenderState(RenderState.AlphaTestEnable, true);
			RenderSinglePass((int)RenderPass.Mask);

			// ALPHA PASS
			world = Matrix.Identity;
			ApplyMatrices3D();
			graphics.Device.SetRenderState(RenderState.AlphaBlendEnable, true);
			graphics.Device.SetRenderState(RenderState.AlphaTestEnable, false);
			graphics.Device.SetRenderState(RenderState.ZWriteEnable, false);
			graphics.Device.SetRenderState(RenderState.SourceBlend, Blend.SourceAlpha);
			graphics.Device.SetRenderState(RenderState.DestinationBlend, Blend.InverseSourceAlpha);
			RenderSinglePass((int)RenderPass.Alpha);

			// THINGS
			if(renderthingcages) RenderThingCages();
            RenderThingArrows();    // villsa
			
			// ADDITIVE PASS
			world = Matrix.Identity;
			ApplyMatrices3D();
			graphics.Device.SetRenderState(RenderState.DestinationBlend, Blend.One);
			RenderSinglePass((int)RenderPass.Additive);
			
			// Remove references
			graphics.Shaders.World3D.Texture1 = null;
			
			// Done
			graphics.Shaders.World3D.End();
			
			// Release the references to the geometry, the collections are reused for the next frame
			RecycleGeometry();
		}

        // villsa 9/15/11
        private void RenderThingArrows()
        {
            int currentshaderpass = shaderpass;
            int highshaderpass = shaderpass + 2;

            // Set renderstates
            graphics.Device.SetRenderState(RenderState.AlphaBlendEnable, true);
            graphics.Device.SetRenderState(RenderState.AlphaTestEnable, false);
            graphics.Device.SetRenderState(RenderState.ZWriteEnable, false);
            graphics.Device.SetRenderState(RenderState.SourceBlend, Blend.SourceAlpha);
            graphics.Device.SetRenderState(RenderState.DestinationBlend, Blend.InverseSourceAlpha);
            graphics.Device.SetStreamSource(0, thingarrow, 0, WorldVertex.Stride);
            graphics.Device.SetTexture(0, General.Map.Data.WhiteTexture.Texture);
            graphics.Shaders.World3D.Texture1 = General.Map.Data.WhiteTexture.Texture;

            graphics.Shaders.World3D.BeginPass(shaderpass);
            foreach (VisualThing t in thingsbydistance)
            {
                // Things that have no direction don't get an arrow
                if (t.Thing.IconOffset != 0f) continue;

                // Determine the shader pass we want to use for this object
                int wantedshaderpass = (((t == highlighted) && showhighlight) || (t.Selected && showselection)) ? highshaderpass : shaderpass;

                // Switch shader pass?
                if (currentshaderpass != wantedshaderpass)
                {
                    graphics.Shaders.World3D.EndPass();
                    graphics.Shaders.World3D.BeginPass(wantedshaderpass);
                    currentshaderpass = wantedshaderpass;
                }

                // Setup matrix
                world = Matrix.Multiply(t.CageScales, t.Position);
                Matrix rot = Matrix.RotationYawPitchRoll(0.0f, 0.0f, t.Thing.Angle + Angle2D.PI);
                world = Matrix.Multiply(rot, world);
                ApplyMatrices3D();

                // Setup color
                if (currentshaderpass == highshaderpass)
                {
                    Color4 highcolor = CalculateHighlightColor((t == highlighted) && showhighlight, t.Selected && showselection);
                    graphics.Shaders.World3D.SetHighlightColor(highcolor.ToArgb());
                    highcolor.Alpha = 1.0f;
                    graphics.Shaders.World3D.SetModulateColor(highcolor.ToArgb());
                    graphics.Device.SetRenderState(RenderState.TextureFactor, highcolor.ToArgb());
                }
                else
                {
                    graphics.Shaders.World3D.SetModulateColor(t.CageColor);
                    graphics.Device.SetRenderState(RenderState.TextureFactor, t.CageColor);
                }

                // Render!
                graphics.Shaders.World3D.ApplySettings();
                graphics.Device.DrawPrimitives(PrimitiveType.LineList, 0, 3);
            }

            // Done
            graphics.Shaders.World3D.EndPass();
            graphics.Shaders.World3D.SetModulateColor(-1);
            graphics.Device.SetRenderState(RenderState.TextureFactor, -1);
        }

		// This renders all thing cages
		private void RenderThingCages()
		{
			int currentshaderpass = shaderpass;
			int highshaderpass = shaderpass + 2;

			// Set renderstates
			graphics.Device.SetRenderState(RenderState.AlphaBlendEnable, true);
			graphics.Device.SetRenderState(RenderState.AlphaTestEnable, false);
			graphics.Device.SetRenderState(RenderState.ZWriteEnable, false);
			graphics.Device.SetRenderState(RenderState.SourceBlend, Blend.SourceAlpha);
			graphics.Device.SetRenderState(RenderState.DestinationBlend, Blend.InverseSourceAlpha);
			graphics.Device.SetStreamSource(0, thingcage, 0, WorldVertex.Stride);
			graphics.Device.SetTexture(0, General.Map.Data.ThingBox.Texture);
			graphics.Shaders.World3D.Texture1 = General.Map.Data.ThingBox.Texture;

			graphics.Shaders.World3D.BeginPass(shaderpass);
			foreach(VisualThing t in thingsbydistance)
			{
				// Determine the shader pass we want to use for this object
				int wantedshaderpass = (((t == highlighted) && showhighlight) || (t.Selected && showselection)) ? highshaderpass : shaderpass;

				// Switch shader pass?
				if(currentshaderpass != wantedshaderpass)
				{
					graphics.Shaders.World3D.EndPass();
					graphics.Shaders.World3D.BeginPass(wantedshaderpass);
					currentshaderpass = wantedshaderpass;
				}

				// Setup matrix
				world = Matrix.Multiply(t.CageScales, t.Position);
				ApplyMatrices3D();

				// Setup color
				if(currentshaderpass == highshaderpass)
				{
					Color4 highcolor = CalculateHighlightColor((t == highlighted) && showhighlight, t.Selected && showselection);
					graphics.Shaders.World3D.SetHighlightColor(highcolor.ToArgb());
					highcolor.Alpha = 1.0f;
					graphics.Shaders.World3D.SetModulateColor(highcolor.ToArgb());
					graphics.Device.SetRenderState(RenderState.TextureFactor, highcolor.ToArgb());
				}
				else
				{
					graphics.Shaders.World3D.SetModulateColor(t.CageColor);
					graphics.Device.SetRenderState(RenderState.TextureFactor, t.CageColor);
				}

				// Render!
				graphics.Shaders.World3D.ApplySettings();
				graphics.Device.DrawPrimitives(PrimitiveType.TriangleList, 0, 12);
			}

			// Done
			graphics.Shaders.World3D.EndPass();
			graphics.Shaders.World3D.SetModulateColor(-1);
			graphics.Device.SetRenderState(RenderState.TextureFactor, -1);
		}

		// This performs a single render pass
		private void RenderSinglePass(int pass)
		{
			int currentshaderpass = shaderpass;
			int highshaderpass = shaderpass + 2;
			
			// Get geometry for this pass
			Dictionary<ImageData, BinaryHeap<VisualGeometry>> geopass = geometry[pass];
			
			// Texture mirroring is only used by Doom 64 maps. The sampler state is only changed when
			// it actually differs from what is currently set, instead of for every single draw call.
			bool doom64mode = General.Map.FormatInterface.InDoom64Mode;
			bool mirroru = false;
			bool mirrorv = false;
			Linedef lastflagsline = null;
			bool lastlinemirroru = false;
			bool lastlinemirrorv = false;
			if(doom64mode)
			{
				graphics.Device.SetSamplerState(0, SamplerState.AddressU, TextureAddress.Wrap);
				graphics.Device.SetSamplerState(0, SamplerState.AddressV, TextureAddress.Wrap);
			}
			
			// The effect settings only need to be committed when something changed since the last draw
			bool settingsdirty = true;
			int lasthighlightcolor = 0;
			bool havehighlightcolor = false;
			float lastglow = 0.0f;
			bool haveglow = false;
			float lastscrollu = 0.0f;
			float lastscrollv = 0.0f;
			float lastscrollz = 0.0f;

			// Begin rendering with this shader
			graphics.Shaders.World3D.BeginPass(shaderpass);
			
			// Render the geometry collected
			foreach(KeyValuePair<ImageData, BinaryHeap<VisualGeometry>> group in geopass)
			{
				ImageData curtexture;

				// Doom 64 animated textures (ANIMDEFS) show the current frame instead
				ImageData grouptexture = group.Key;
				if(doom64mode && (grouptexture != null) && (General.Map.Data.Animations != null))
					grouptexture = General.Map.Data.Animations.Translate(grouptexture);

				// What texture to use?
				if(grouptexture is UnknownImage)
					curtexture = General.Map.Data.UnknownTexture3D;
				else if((grouptexture != null) && grouptexture.IsImageLoaded && !grouptexture.IsDisposed)
					curtexture = grouptexture;
				else
					curtexture = General.Map.Data.Hourglass3D;

				// Create Direct3D texture if still needed
				if((curtexture.Texture == null) || curtexture.Texture.Disposed)
					curtexture.CreateTexture();

				// Texture changed, so the effect settings must be committed again
				settingsdirty = true;
				
				// Apply texture
                if (showlightonly)  // villsa
                {
                    if (!graphics.Shaders.Enabled) graphics.Device.SetTexture(0, General.Map.Data.WhiteTexture.Texture);
                    graphics.Shaders.World3D.Texture1 = General.Map.Data.WhiteTexture.Texture;
                }
                else
                {
                    if (!graphics.Shaders.Enabled) graphics.Device.SetTexture(0, curtexture.Texture);
                    graphics.Shaders.World3D.Texture1 = curtexture.Texture;
                }
				
				// Go for all geometry that uses this texture
				VisualSector sector = null;
				foreach(VisualGeometry g in group.Value)
				{
					// Changing sector?
					if(!object.ReferenceEquals(g.Sector, sector))
					{
						// Update the sector if needed
						if(g.Sector.NeedsUpdateGeo) g.Sector.Update();

						// Only do this sector when a vertexbuffer is created
						if(g.Sector.GeometryBuffer != null)
						{
							// Change current sector
							sector = g.Sector;

							// Set stream source
							graphics.Device.SetStreamSource(0, sector.GeometryBuffer, 0, WorldVertex.Stride);
						}
						else
						{
							sector = null;
						}
					}	
					
					if(sector != null)
					{
						// Determine the shader pass we want to use for this object
						int wantedshaderpass = (((g == highlighted) && showhighlight) || (g.Selected && showselection)) ? highshaderpass : shaderpass;
						
						// Switch shader pass?
						if(currentshaderpass != wantedshaderpass)
						{
							graphics.Shaders.World3D.EndPass();
							graphics.Shaders.World3D.BeginPass(wantedshaderpass);
							currentshaderpass = wantedshaderpass;
							settingsdirty = true;
						}

                        // villsa - mirror UVs if special flags are set
                        if (doom64mode)
                        {
                            // Find out what is wanted for this line (the flags are only looked up when the line changes)
                            Linedef gline = (g.Sidedef != null) ? g.Sidedef.Line : null;
                            if (!object.ReferenceEquals(gline, lastflagsline))
                            {
                                lastflagsline = gline;
                                lastlinemirroru = (gline != null) && gline.IsFlagSet("1073741824");
                                lastlinemirrorv = (gline != null) && gline.IsFlagSet("2147483648");
                            }

                            // Only change the sampler state when it differs from the current state
                            if (lastlinemirroru != mirroru)
                            {
                                mirroru = lastlinemirroru;
                                graphics.Device.SetSamplerState(0, SamplerState.AddressU, mirroru ? TextureAddress.Mirror : TextureAddress.Wrap);
                            }
                            if (lastlinemirrorv != mirrorv)
                            {
                                mirrorv = lastlinemirrorv;
                                graphics.Device.SetSamplerState(0, SamplerState.AddressV, mirrorv ? TextureAddress.Mirror : TextureAddress.Wrap);
                            }
                        }
						
						// Set the colors to use
						if(!graphics.Shaders.Enabled)
						{
							graphics.Device.SetTexture(2, (g.Selected && showselection) ? selectionimage.Texture : null);
							graphics.Device.SetTexture(3, ((g == highlighted) && showhighlight) ? highlightimage.Texture : null);
						}
						else
						{
							// Only update the highlight color when it differs from the previous draw
							int highlightargb = CalculateHighlightColor((g == highlighted) && showhighlight, (g.Selected && showselection)).ToArgb();
							if(!havehighlightcolor || (highlightargb != lasthighlightcolor))
							{
								graphics.Shaders.World3D.SetHighlightColor(highlightargb);
								lasthighlightcolor = highlightargb;
								havehighlightcolor = true;
								settingsdirty = true;
							}
							
							// Doom 64 glowing sector specials add light to the texture, only update when it differs
							float glow = SectorGlow.GetGlow(g.Sector.Sector);
							if(!haveglow || (glow != lastglow))
							{
								graphics.Shaders.World3D.SetGlowLevel(glow);
								lastglow = glow;
								haveglow = true;
								settingsdirty = true;
							}
							
							// Doom 64 scrolling textures, only update when the offset differs from the previous draw
							float scrollu, scrollv, scrollz;
							TextureScroll.GetOffset(g, out scrollu, out scrollv, out scrollz);
							if((scrollu != lastscrollu) || (scrollv != lastscrollv) || (scrollz != lastscrollz))
							{
								graphics.Shaders.World3D.SetUVOffset(scrollu, scrollv, scrollz);
								lastscrollu = scrollu;
								lastscrollv = scrollv;
								lastscrollz = scrollz;
								settingsdirty = true;
							}
							
							// Commit the effect settings, but only when something changed
							if(settingsdirty)
							{
								graphics.Shaders.World3D.ApplySettings();
								settingsdirty = false;
							}
						}
						
						// Render!
						graphics.Device.DrawPrimitives(PrimitiveType.TriangleList, g.VertexOffset, g.Triangles);
					}
				}
			}
			
			// villsa - reset samplers to default wrappings
			if(doom64mode && (mirroru || mirrorv))
			{
				graphics.Device.SetSamplerState(0, SamplerState.AddressU, TextureAddress.Wrap);
				graphics.Device.SetSamplerState(0, SamplerState.AddressV, TextureAddress.Wrap);
			}

			// Things are never scrolled, so make sure nothing is left over from the geometry
			if((lastscrollu != 0.0f) || (lastscrollv != 0.0f) || (lastscrollz != 0.0f)) graphics.Shaders.World3D.SetUVOffset(0.0f, 0.0f);

			// Things set their own glow below, so make sure nothing is left over from the geometry
			if(haveglow && (lastglow != 0.0f)) graphics.Shaders.World3D.SetGlowLevel(0.0f);

			// Get things for this pass
			Dictionary<ImageData, List<VisualThing>> thingspass = things[pass];
			if(thingspass.Count > 0)
			{
				// Texture addressing
				graphics.Device.SetSamplerState(0, SamplerState.AddressU, TextureAddress.Clamp);
				graphics.Device.SetSamplerState(0, SamplerState.AddressV, TextureAddress.Clamp);
				graphics.Device.SetSamplerState(0, SamplerState.AddressW, TextureAddress.Clamp);

				// Render things collected
				foreach(KeyValuePair<ImageData, List<VisualThing>> group in thingspass)
				{
					ImageData curtexture;

					if(!(group.Key is UnknownImage))
					{
						// What texture to use?
						if((group.Key != null) && group.Key.IsImageLoaded && !group.Key.IsDisposed)
							curtexture = group.Key;
						else
							curtexture = General.Map.Data.Hourglass3D;

						// Create Direct3D texture if still needed
						if((curtexture.Texture == null) || curtexture.Texture.Disposed)
							curtexture.CreateTexture();

						// Apply texture
						if(!graphics.Shaders.Enabled) graphics.Device.SetTexture(0, curtexture.Texture);
						graphics.Shaders.World3D.Texture1 = curtexture.Texture;

						// Render all things with this texture
						foreach(VisualThing t in group.Value)
						{
							// Update buffer if needed
							t.Update();

							// Only do this sector when a vertexbuffer is created
							if(t.GeometryBuffer != null)
							{
								// Determine the shader pass we want to use for this object
								int wantedshaderpass = (((t == highlighted) && showhighlight) || (t.Selected && showselection)) ? highshaderpass : shaderpass;

								// Switch shader pass?
								if(currentshaderpass != wantedshaderpass)
								{
									graphics.Shaders.World3D.EndPass();
									graphics.Shaders.World3D.BeginPass(wantedshaderpass);
									currentshaderpass = wantedshaderpass;
								}

								// Set the colors to use
								if(!graphics.Shaders.Enabled)
								{
									graphics.Device.SetTexture(2, (t.Selected && showselection) ? selectionimage.Texture : null);
									graphics.Device.SetTexture(3, ((t == highlighted) && showhighlight) ? highlightimage.Texture : null);
								}
								else
								{
									graphics.Shaders.World3D.SetHighlightColor(CalculateHighlightColor((t == highlighted) && showhighlight, (t.Selected && showselection)).ToArgb());

									// Things standing in a glowing sector glow with it (camera and trigger icons are fullbright)
									Thing thing = t.Thing;
									bool thingglows = (thing != null) && (thing.Type != 0) && (thing.Type != 89);
									graphics.Shaders.World3D.SetGlowLevel(thingglows ? SectorGlow.GetGlow(thing.Sector) : 0.0f);
								}

								// Create the matrix for positioning / rotation
								world = t.Orientation;
								if(t.Billboard) world = Matrix.Multiply(world, billboard);
								world = Matrix.Multiply(world, t.Position);
								ApplyMatrices3D();
								graphics.Shaders.World3D.ApplySettings();

								// Apply buffer
								graphics.Device.SetStreamSource(0, t.GeometryBuffer, 0, WorldVertex.Stride);

								// Render!
								graphics.Device.DrawPrimitives(PrimitiveType.TriangleList, 0, t.Triangles);
							}
						}
					}
				}
				
				// Texture addressing
				graphics.Device.SetSamplerState(0, SamplerState.AddressU, TextureAddress.Wrap);
				graphics.Device.SetSamplerState(0, SamplerState.AddressV, TextureAddress.Wrap);
				graphics.Device.SetSamplerState(0, SamplerState.AddressW, TextureAddress.Wrap);
			}

			// Don't let the glow or scrolling leak into whatever is rendered next
			graphics.Shaders.World3D.SetGlowLevel(0.0f);
			graphics.Shaders.World3D.SetUVOffset(0.0f, 0.0f);

			// Done rendering with this shader
			graphics.Shaders.World3D.EndPass();
		}

		// This calculates the highlight/selection color
		public Color4 CalculateHighlightColor(bool ishighlighted, bool isselected)
		{
			Color4 highlightcolor = isselected ? General.Colors.Selection.ToColorValue() : General.Colors.Highlight.ToColorValue();
			highlightcolor.Alpha = ishighlighted ? highlightglowinv : highlightglow;
			return highlightcolor;
		}
		
		// This finishes rendering
		public void Finish()
		{
            General.Plugins.OnPresentDisplayBegin();

            // Done
            graphics.FinishRendering();
			graphics.Present();
			highlighted = null;
		}
		
		#endregion
		
		#region ================== Rendering
		
		// This sets the highlighted object for the rendering
		public void SetHighlightedObject(IVisualPickable obj)
		{
			highlighted = obj;
		}
		
		// This collects a visual sector's geometry for rendering
		public void AddSectorGeometry(VisualGeometry g)
		{
			// Must have a texture and vertices
			if((g.Texture != null) && (g.Triangles > 0))
			{
				Dictionary<ImageData, BinaryHeap<VisualGeometry>> geopass = geometry[g.RenderPassInt];
				BinaryHeap<VisualGeometry> group;
				
				// Texture group not yet collected?
				if(!geopass.TryGetValue(g.Texture, out group))
				{
					// Create texture group (reuse an old one when possible)
					group = (geometryheappool.Count > 0) ? geometryheappool.Pop() : new BinaryHeap<VisualGeometry>();
					geopass.Add(g.Texture, group);
				}
				
				// Add geometry to texture group
				group.Add(g);
			}
		}

		// This collects a visual sector's geometry for rendering
		public void AddThingGeometry(VisualThing t)
		{
			// Select the sprite rotation that faces the camera
			t.UpdateSpriteFrame();

			// Make sure the distance to camera is calculated
			t.CalculateCameraDistance(cameraposition);
			thingsbydistance.Add(t);
			
			// Must have a texture!
			if(t.Texture != null)
			{
				Dictionary<ImageData, List<VisualThing>> thingspass = things[t.RenderPassInt];
				List<VisualThing> group;
				
				// Texture group not yet collected?
				if(!thingspass.TryGetValue(t.Texture, out group))
				{
					// Create texture group (reuse an old one when possible)
					group = (thinglistpool.Count > 0) ? thinglistpool.Pop() : new List<VisualThing>();
					thingspass.Add(t.Texture, group);
				}

				// Add geometry to texture group
				group.Add(t);
			}
		}

		// This renders the crosshair
		public void RenderCrosshair()
		{
			// Set renderstates
			graphics.Device.SetRenderState(RenderState.CullMode, Cull.None);
			graphics.Device.SetRenderState(RenderState.ZEnable, false);
			graphics.Device.SetRenderState(RenderState.AlphaBlendEnable, true);
			graphics.Device.SetRenderState(RenderState.AlphaTestEnable, false);
			graphics.Device.SetRenderState(RenderState.SourceBlend, Blend.SourceAlpha);
			graphics.Device.SetRenderState(RenderState.DestinationBlend, Blend.InverseSourceAlpha);
			graphics.Device.SetRenderState(RenderState.TextureFactor, -1);
			graphics.Device.SetTransform(TransformState.World, Matrix.Identity);
			graphics.Device.SetTransform(TransformState.Projection, Matrix.Identity);
			ApplyMatrices2D();
			
			// Texture
			if(crosshairbusy)
			{
				if(General.Map.Data.CrosshairBusy3D.Texture == null) General.Map.Data.CrosshairBusy3D.CreateTexture();
				graphics.Device.SetTexture(0, General.Map.Data.CrosshairBusy3D.Texture);
				graphics.Shaders.Display2D.Texture1 = General.Map.Data.CrosshairBusy3D.Texture;
			}
			else
			{
				if(General.Map.Data.Crosshair3D.Texture == null) General.Map.Data.Crosshair3D.CreateTexture();
				graphics.Device.SetTexture(0, General.Map.Data.Crosshair3D.Texture);
				graphics.Shaders.Display2D.Texture1 = General.Map.Data.Crosshair3D.Texture;
			}
			
			// Draw
			graphics.Shaders.Display2D.Begin();
			graphics.Shaders.Display2D.SetSettings(1.0f, 1.0f, 0.0f, 1.0f, true);
			graphics.Shaders.Display2D.BeginPass(1);
			graphics.Device.DrawUserPrimitives<FlatVertex>(PrimitiveType.TriangleStrip, 0, 2, crosshairverts);
			graphics.Shaders.Display2D.EndPass();
			graphics.Shaders.Display2D.End();
		}

		// This switches fog on and off
		public void SetFogMode(bool usefog)
		{
			graphics.Device.SetRenderState(RenderState.FogEnable, usefog);
		}

		// This siwtches crosshair busy icon on and off
		public void SetCrosshairBusy(bool busy)
		{
			crosshairbusy = busy;
		}
		
		#endregion
	}
}
