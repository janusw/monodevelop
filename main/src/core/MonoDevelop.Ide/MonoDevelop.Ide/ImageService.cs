// ImageService.cs
//
// Author:
//   Mike Krüger <mkrueger@novell.com>
//
// Copyright (C) 2009 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using Mono.Addins;
using System.IO;
using MonoDevelop.Ide.Extensions;
using MonoDevelop.Core;
using MonoDevelop.Components;
using System.Text;
using System.Linq;
using MonoDevelop.Ide.Gui.Components;
using System.Threading.Tasks;
using System.Net;
using Xwt.Backends;

namespace MonoDevelop.Ide
{
	public static class ImageService
	{
		const string IconsExtensionPath = "/MonoDevelop/Core/StockIcons";

		static Gtk.IconFactory iconFactory = new Gtk.IconFactory ();

		// Mapping of icon spec to stock icon id.
		static List<Dictionary<string, string>> addinIcons = new List<Dictionary<string, string>> ();

		// Mapping of icon spec to stock icon id, but used only when the icon is not bound to a specific add-in
		static Dictionary<string, string> iconSpecToStockId = new Dictionary<string, string> ();

		// Map of all animations
		static Dictionary<string, AnimatedIcon> animationFactory = new Dictionary<string, AnimatedIcon> ();

		static List<RuntimeAddin> addins = new List<RuntimeAddin> ();
		static Dictionary<string, string> composedIcons = new Dictionary<string, string> ();

		// Dictionary of extension nodes by stock icon id. It holds nodes that have not yet been loaded
		static Dictionary<string, List<StockIconCodon>> iconStock = new Dictionary<string, List<StockIconCodon>> ();

		static Gtk.Requisition[] iconSizes = new Gtk.Requisition[7];

		static ImageService ()
		{
			iconFactory.AddDefault ();
			IconId.IconNameRequestHandler = delegate (string stockId) {
				EnsureStockIconIsLoaded (stockId);
			};

			AddinManager.AddExtensionNodeHandler (IconsExtensionPath, delegate (object sender, ExtensionNodeEventArgs args) {
				StockIconCodon iconCodon = (StockIconCodon)args.ExtensionNode;
				switch (args.Change) {
				case ExtensionChange.Add:
					if (!iconStock.ContainsKey (iconCodon.StockId))
						iconStock[iconCodon.StockId] = new List<StockIconCodon> ();
					iconStock[iconCodon.StockId].Add (iconCodon);
					break;
				}
			});

			for (int i = 0; i < iconSizes.Length; i++) {
				int w, h;
				if (!Gtk.Icon.SizeLookup ((Gtk.IconSize)i, out w, out h))
					w = h = -1;
				iconSizes[i].Width = w;
				iconSizes[i].Height = h;
			}
			if (Platform.IsWindows) {
				iconSizes[(int)Gtk.IconSize.Menu].Width = 16;
				iconSizes[(int)Gtk.IconSize.Menu].Height = 16;
			}

			// Preload icons defined in MD.Ide. Ensures that the gtk icon overrides are available.
			var current = AddinManager.CurrentAddin;
			foreach (var id in AddinManager.GetExtensionNodes (IconsExtensionPath).OfType<StockIconCodon> ().Where (c => c.Addin == current).Select (c => c.StockId).Distinct ())
				EnsureStockIconIsLoaded (id);
		}

		static Xwt.Drawing.Image LoadStockIcon (StockIconCodon iconCodon, bool forceWildcard)
		{
			try {
				Gdk.Pixbuf pixbuf = null, pixbuf2x = null;
				AnimatedIcon animatedIcon = null;
				Func<Stream[]> imageLoader = null;

				if (!string.IsNullOrEmpty (iconCodon.Resource) || !string.IsNullOrEmpty (iconCodon.File)) {
					// using the stream directly produces a gdk warning.
					byte[] buffer;

					if (iconCodon.Resource != null) {
						imageLoader = delegate {
							var stream = iconCodon.Addin.GetResource (iconCodon.Resource);
							var stream2x = iconCodon.Addin.GetResource2x (iconCodon.Resource);
							if (stream2x == null)
								return new [] { stream };
							else
								return new [] { stream, stream2x };
						};
					}
					else {
						imageLoader = delegate {
							var file = iconCodon.Addin.GetFilePath (iconCodon.File);
							var stream = File.OpenRead (file);
							Stream stream2x = null;
							var file2x = Path.Combine (Path.GetDirectoryName (file), Path.GetFileNameWithoutExtension (file) + "@2x" + Path.GetExtension (file));
							if (File.Exists (file2x))
								stream2x = File.OpenRead (file2x);
							else {
								file2x = file + "@2x";
								if (File.Exists (file2x))
									stream2x = File.OpenRead (file2x);
							}
							if (stream2x == null)
								return new [] { stream };
							else
								return new [] { stream, stream2x };
						};
					}
					var streams = imageLoader ();

					var st = streams[0];
					var st2x = streams.Length > 1 ? streams[1] : null;

					using (st) {
						if (st == null || st.Length < 0) {
							LoggingService.LogError ("Did not find resource '{0}' in addin '{1}' for icon '{2}'",
								iconCodon.Resource, iconCodon.Addin.Id, iconCodon.StockId);
							return null;
						}
						buffer = new byte [st.Length];
						st.Read (buffer, 0, (int)st.Length);
					}
					pixbuf = new Gdk.Pixbuf (buffer);

					using (st2x) {
						if (st2x != null && st2x.Length >= 0) {
							buffer = new byte [st2x.Length];
							st2x.Read (buffer, 0, (int)st2x.Length);
							pixbuf2x = new Gdk.Pixbuf (buffer);
						}
					}

				} else if (!string.IsNullOrEmpty (iconCodon.IconId)) {
					var id = GetStockIdForImageSpec (iconCodon.Addin, iconCodon.IconId, iconCodon.IconSize);
					pixbuf = GetPixbuf (id, iconCodon.IconSize);
					pixbuf2x = Get2xIconVariant (pixbuf);
					// This may be an animation, get it
					animationFactory.TryGetValue (id, out animatedIcon);
				} else if (!string.IsNullOrEmpty (iconCodon.Animation)) {
					string id = GetStockIdForImageSpec (iconCodon.Addin, "animation:" + iconCodon.Animation, iconCodon.IconSize);
					pixbuf = GetPixbuf (id, iconCodon.IconSize);
					// This *should* be an animation
					animationFactory.TryGetValue (id, out animatedIcon);
				}

				Gtk.IconSize size = forceWildcard? Gtk.IconSize.Invalid : iconCodon.IconSize;
				if (pixbuf != null)
					AddToIconFactory (iconCodon.StockId, pixbuf, pixbuf2x, size);

				if (animatedIcon != null)
					AddToAnimatedIconFactory (iconCodon.StockId, animatedIcon);

				var img = Xwt.Toolkit.CurrentEngine.WrapImage (pixbuf);
				if (pixbuf2x != null) {
					var img2x = Xwt.Toolkit.CurrentEngine.WrapImage (pixbuf2x);
					img = Xwt.Drawing.Image.CreateMultiResolutionImage (new [] { img, img2x });
				}
				if (imageLoader != null)
					img.SetStreamSource (imageLoader);
				return img;

			} catch (Exception ex) {
				LoggingService.LogError (string.Format ("Error loading icon '{0}'", iconCodon.StockId), ex);
				return null;
			}
		}

		public static void Initialize ()
		{
			//forces static constructor to run
		}

		static Gdk.Pixbuf Get2xIconVariant (Gdk.Pixbuf px)
		{
			return Mono.TextEditor.GtkWorkarounds.Get2xVariant (px);
		}

		static void Set2xIconVariant (Gdk.Pixbuf px, Gdk.Pixbuf variant2x)
		{
			Mono.TextEditor.GtkWorkarounds.Set2xVariant (px, variant2x);
		}

		static Dictionary<string,Xwt.Drawing.Image> icons = new Dictionary<string, Xwt.Drawing.Image> ();

		public static Xwt.Drawing.Image GetIcon (string name, Gtk.IconSize size)
		{
			return GetIcon (name).WithSize (size);
		}

		public static Xwt.Drawing.Image GetIcon (string name)
		{
			return GetIcon (name, true);
		}

		public static Xwt.Drawing.Image GetIcon (string name, bool generateDefaultIcon)
		{
			name = name ?? "";

			Xwt.Drawing.Image img;
			if (icons.TryGetValue (name, out img))
				return img;

			if (string.IsNullOrEmpty (name)) {
				LoggingService.LogWarning ("Empty icon requested. Stack Trace: " + Environment.NewLine + Environment.StackTrace);
				icons [name] = img = CreateColorIcon ("#FF0000");
				return img;
			}

			//if an icon name begins with '#', we assume it's a hex colour
			if (name[0] == '#') {
				icons [name] = img = CreateColorBlock (name, Gtk.IconSize.Dialog).ToXwtImage ();
				return img;
			}

			EnsureStockIconIsLoaded (name);

			// Try again since it may have already been registered
			if (icons.TryGetValue (name, out img))
				return img;

			Gtk.IconSet iconset = Gtk.IconFactory.LookupDefault (name);
			if (iconset == null && !Gtk.IconTheme.Default.HasIcon (name) && generateDefaultIcon) {
				LoggingService.LogWarning ("Unknown icon: " + name);
				return CreateColorIcon ("#FF0000FF");
			}

			return icons [name] = img = Xwt.Toolkit.CurrentEngine.WrapImage (name);
		}

		static Gdk.Pixbuf GetPixbuf (string name, Gtk.IconSize size, bool generateDefaultIcon = true)
		{
			if (string.IsNullOrEmpty (name)) {
				LoggingService.LogWarning ("Empty icon requested. Stack Trace: " + Environment.NewLine + Environment.StackTrace);
				return CreateColorBlock ("#FF0000", size);
			}

			// If this name refers to an icon defined in an extension point, the images for the icon will now be laoded
			EnsureStockIconIsLoaded (name);

			//if an icon name begins with '#', we assume it's a hex colour
			if (name[0] == '#')
				return CreateColorBlock (name, size);

			// Converts an image spec into a real stock icon id
			string stockid = GetStockIdForImageSpec (name, size);

			if (string.IsNullOrEmpty (stockid)) {
				LoggingService.LogWarning ("Can't get stock id for " + name + " : " + Environment.NewLine + Environment.StackTrace);
				return CreateColorBlock ("#FF0000", size);
			}

			Gtk.IconSet iconset = Gtk.IconFactory.LookupDefault (stockid);
			if (iconset != null)
				return iconset.RenderIcon (Gtk.Widget.DefaultStyle, Gtk.TextDirection.Ltr, Gtk.StateType.Normal, size, null, null);

			if (Gtk.IconTheme.Default.HasIcon (stockid)) {
				int h = iconSizes[(int)size].Height;
				Gdk.Pixbuf result = Gtk.IconTheme.Default.LoadIcon (stockid, h, (Gtk.IconLookupFlags)0);
				return result;
			}
			if (generateDefaultIcon) {
				LoggingService.LogWarning ("Can't lookup icon: " + name);
				return CreateColorBlock ("#FF0000FF", size);
			}
			return null;
		}

		internal static void EnsureStockIconIsLoaded (string stockId)
		{
			if (string.IsNullOrEmpty (stockId))
				return;

			List<StockIconCodon> stockIcon;
			if (iconStock.TryGetValue (stockId, out stockIcon)) {
				var frames = new List<Xwt.Drawing.Image> ();
				//determine whether there's a wildcarded image
				bool hasWildcard = false;
				foreach (var i in stockIcon) {
					if (i.IconSize == Gtk.IconSize.Invalid)
						hasWildcard = true;
				}
				//load all the images
				foreach (var i in stockIcon) {
					var si = LoadStockIcon (i, false);
					if (si != null)
						frames.Add (si);
				}
				//if there's no wildcard, find the "biggest" version and make it a wildcard
				if (!hasWildcard) {
					int biggest = 0, biggestSize = iconSizes[(int)stockIcon[0].IconSize].Width;
					for (int i = 1; i < stockIcon.Count; i++) {
						int w = iconSizes[(int)stockIcon[i].IconSize].Width;
						if (w > biggestSize) {
							biggest = i;
							biggestSize = w;
						}
					}
					//	LoggingService.LogWarning ("Stock icon '{0}' registered without wildcarded version.", stockId);
					LoadStockIcon (stockIcon[biggest], true);

				}
				// Icon loaded, it can be removed from the pending icon collection
				iconStock.Remove (stockId);

				if (frames.Count > 0)
					icons [stockId] = Xwt.Drawing.Image.CreateMultiSizeIcon (frames);
			}
		}

		static Xwt.Drawing.Image CreateColorIcon (string name)
		{
			var color = Xwt.Drawing.Color.FromName (name);
			using (var ib = new Xwt.Drawing.ImageBuilder (16, 16)) {
				ib.Context.Rectangle (0, 0, 16, 16);
				ib.Context.SetColor (color);
				ib.Context.Fill ();
				return ib.ToVectorImage ();
			}
		}

		static Gdk.Pixbuf CreateColorBlock (string name, Gtk.IconSize size)
		{
			int w, h;
			if (!Gtk.Icon.SizeLookup (Gtk.IconSize.Menu, out w, out h))
				w = h = 22;
			Gdk.Pixbuf p = new Gdk.Pixbuf (Gdk.Colorspace.Rgb, true, 8, w, h);
			uint color;
			if (!TryParseColourFromHex (name, false, out color))
				//if lookup fails, make it transparent
				color = 0xffffff00u;
			p.Fill (color);
			return p;
		}

		static bool TryParseColourFromHex (string str, bool alpha, out uint val)
		{
			val = 0x0;
			if (str.Length != (alpha ? 9 : 7))
				return false;

			for (int stringIndex = 1; stringIndex < str.Length; stringIndex++) {
				uint bits;
				switch (str[stringIndex]) {
				case '0':
					bits = 0;
					break;
				case '1':
					bits = 1;
					break;
				case '2':
					bits = 2;
					break;
				case '3':
					bits = 3;
					break;
				case '4':
					bits = 4;
					break;
				case '5':
					bits = 5;
					break;
				case '6':
					bits = 6;
					break;
				case '7':
					bits = 7;
					break;
				case '8':
					bits = 8;
					break;
				case '9':
					bits = 9;
					break;
				case 'A':
				case 'a':
					bits = 10;
					break;
				case 'B':
				case 'b':
					bits = 11;
					break;
				case 'C':
				case 'c':
					bits = 12;
					break;
				case 'D':
				case 'd':
					bits = 13;
					break;
				case 'E':
				case 'e':
					bits = 14;
					break;
				case 'F':
				case 'f':
					bits = 15;
					break;
				default:
					return false;
				}

				val = (val << 4) | bits;
			}
			if (!alpha)
				val = (val << 8) | 0xff;
			return true;
		}

		public static Gtk.Image GetImage (string name, Gtk.IconSize size)
		{
			var img = new Gtk.Image ();
			img.LoadIcon (name, size);
			return img;
		}
		/*
		public static string GetStockIdFromResource (RuntimeAddin addin, string id)
		{
			return InternalGetStockIdFromResource (addin, id);
		}
		 */

		public static string GetStockId (string filename)
		{
			return GetStockId (filename, Gtk.IconSize.Invalid);
		}
		public static string GetStockId (string filename, Gtk.IconSize iconSize)
		{
			return GetStockIdForImageSpec (filename, iconSize);
		}

		public static string GetStockId (RuntimeAddin addin, string filename)
		{
			return GetStockId (addin, filename, Gtk.IconSize.Invalid);
		}
		public static string GetStockId (RuntimeAddin addin, string filename, Gtk.IconSize iconSize)
		{
			return GetStockIdForImageSpec (addin, filename, iconSize);
		}

		static void AddToIconFactory (string stockId, Gdk.Pixbuf pixbuf, Gdk.Pixbuf pixbuf2x, Gtk.IconSize iconSize)
		{
			Gtk.IconSet iconSet = iconFactory.Lookup (stockId);
			if (iconSet == null) {
				iconSet = new Gtk.IconSet ();
				iconFactory.Add (stockId, iconSet);
			}

			Gtk.IconSource source = new Gtk.IconSource ();
			Gtk.IconSource source2x = null;

			if (Platform.IsWindows) {
				var pixel_scale = Mono.TextEditor.GtkWorkarounds.GetPixelScale ();
				source.Pixbuf = pixbuf.ScaleSimple ((int)(pixbuf.Width * pixel_scale), (int)(pixbuf.Height * pixel_scale), Gdk.InterpType.Bilinear);
			} else {
				source.Pixbuf = pixbuf;
			}

			source.Size = iconSize;
			source.SizeWildcarded = iconSize == Gtk.IconSize.Invalid;

			if (pixbuf2x != null) {
				if (Mono.TextEditor.GtkWorkarounds.SetSourceScale (source, 1)) {
					Mono.TextEditor.GtkWorkarounds.SetSourceScaleWildcarded (source, false);
					source2x = new Gtk.IconSource ();
					source2x.Pixbuf = pixbuf2x;
					source2x.Size = iconSize;
					source2x.SizeWildcarded = iconSize == Gtk.IconSize.Invalid;
					Mono.TextEditor.GtkWorkarounds.SetSourceScale (source2x, 2);
					Mono.TextEditor.GtkWorkarounds.SetSourceScaleWildcarded (source2x, false);
				}
			} else {
				Mono.TextEditor.GtkWorkarounds.SetSourceScaleWildcarded (source, true);
			}

			iconSet.AddSource (source);
			if (source2x != null)
				iconSet.AddSource (source2x);
		}

		static void AddToAnimatedIconFactory (string stockId, AnimatedIcon aicon)
		{
			animationFactory [stockId] = aicon;
		}

		static string InternalGetStockIdFromResource (RuntimeAddin addin, string id, Gtk.IconSize size)
		{
			if (!id.StartsWith ("res:", StringComparison.Ordinal))
				return id;

			id = id.Substring (4);
			int addinId = GetAddinId (addin);
			Dictionary<string, string> hash = addinIcons[addinId];
			string stockId = "__asm" + addinId + "__" + id + "__" + size;
			if (!hash.ContainsKey (stockId)) {
				System.IO.Stream stream = addin.GetResource (id);
				if (stream != null) {
					Gdk.Pixbuf pix1, pix2 = null;
					using (stream) {
						pix1 = new Gdk.Pixbuf (stream);
					}
					stream = addin.GetResource2x (id);
					if (stream != null) {
						using (stream) {
							pix2 = new Gdk.Pixbuf (stream);
						}
					}
					AddToIconFactory (stockId, pix1, pix2, size);
				}
				hash[stockId] = stockId;
			}
			return stockId;
		}

		static Stream GetResource2x (this RuntimeAddin addin, string id)
		{
			var stream = addin.GetResource (Path.GetFileNameWithoutExtension (id) + "@2x" + Path.GetExtension (id));
			if (stream == null)
				stream = addin.GetResource (id + "@2x");
			return stream;
		}

		static string InternalGetStockIdFromAnimation (RuntimeAddin addin, string id, Gtk.IconSize size)
		{
			if (!id.StartsWith ("animation:"))
				return id;

			id = id.Substring (10);
			Dictionary<string, string> hash;
			int addinId;

			if (addin != null) {
				addinId = GetAddinId (addin);
				hash = addinIcons[addinId];
			} else {
				addinId = -1;
				hash = iconSpecToStockId;
			}

			string stockId = "__asm" + addinId + "__" + id + "__" + size;
			if (!hash.ContainsKey (stockId)) {
				var aicon = new AnimatedIcon (addin, id, size);
				AddToIconFactory (stockId, aicon.FirstFrame.WithSize (size).ToPixbuf (), null, size);
				AddToAnimatedIconFactory (stockId, aicon);
				hash[stockId] = stockId;
			}
			return stockId;
		}

		static int GetAddinId (RuntimeAddin addin)
		{
			int result = addins.IndexOf (addin);
			if (result == -1) {
				result = addins.Count;
				addinIcons.Add (new Dictionary<string, string> ());
			}
			return result;
		}

		static string GetComposedIcon (string[] ids, Gtk.IconSize size)
		{
			string id = string.Join ("_", ids);
			string cid;
			if (composedIcons.TryGetValue (id, out cid))
				return cid;
			System.Collections.ICollection col = size == Gtk.IconSize.Invalid ? Enum.GetValues (typeof(Gtk.IconSize)) : new object [] { size };
			foreach (Gtk.IconSize sz in col) {
				if (sz == Gtk.IconSize.Invalid)
					continue;
				Xwt.Drawing.ImageBuilder ib = null;
				Xwt.Drawing.Image icon = null;
				for (int n = 0; n < ids.Length; n++) {
					var px = GetIcon (ids[n], sz);
					if (px == null) {
						LoggingService.LogError ("Error creating composed icon {0} at size {1}. Icon {2} is missing.", id, sz, ids[n]);
						icon = null;
						break;
					}

					if (n == 0) {
						ib = new Xwt.Drawing.ImageBuilder (px.Width, px.Height);
						ib.Context.DrawImage (px, 0, 0);
						icon = px;
						continue;
					}

					if (icon.Width != px.Width || icon.Height != px.Height)
						px = px.WithSize (icon.Width, icon.Height);

					ib.Context.DrawImage (px, 0, 0);
				}
				if (icon != null)
					AddToIconFactory (id, ib.ToBitmap ().ToPixbuf (), ib.ToBitmap (2f).WithSize (icon.Width * 2, icon.Height * 2).ToPixbuf (), sz);
			}
			composedIcons[id] = id;
			return id;
		}

		static string GetStockIdForImageSpec (string filename, Gtk.IconSize size)
		{
			return GetStockIdForImageSpec (null, filename, size);
		}

		static string GetStockIdForImageSpec (RuntimeAddin addin, string filename, Gtk.IconSize size)
		{
			if (filename.IndexOf ('|') == -1)
				return PrivGetStockId (addin, filename, size);

			string[] parts = filename.Split ('|');
			for (int n = 0; n < parts.Length; n++) {
				parts[n] = PrivGetStockId (addin, parts[n], size);
			}
			return GetComposedIcon (parts, size);
		}

		static string PrivGetStockId (RuntimeAddin addin, string filename, Gtk.IconSize size)
		{
			if (addin != null && filename.StartsWith ("res:"))
				return InternalGetStockIdFromResource (addin, filename, size);

			if (filename.StartsWith ("animation:"))
				return InternalGetStockIdFromAnimation (addin, filename, size);

			return filename;
		}

		public static bool IsAnimation (string iconId, Gtk.IconSize size)
		{
			EnsureStockIconIsLoaded (iconId);
			string id = GetStockIdForImageSpec (iconId, size);
			return animationFactory.ContainsKey (id);
		}

		public static AnimatedIcon GetAnimatedIcon (string iconId)
		{
			return GetAnimatedIcon (iconId, Gtk.IconSize.Button);
		}

		public static AnimatedIcon GetAnimatedIcon (string iconId, Gtk.IconSize size)
		{
			EnsureStockIconIsLoaded (iconId);
			string id = GetStockIdForImageSpec (iconId, size);

			AnimatedIcon aicon;
			animationFactory.TryGetValue (id, out aicon);
			return aicon;
		}

		static List<WeakReference> animatedImages = new List<WeakReference> ();

		class AnimatedImageInfo {
			public Gtk.Image Image;
			public AnimatedIcon AnimatedIcon;
			public IDisposable Animation;

			public AnimatedImageInfo (Gtk.Image img, AnimatedIcon anim)
			{
				Image = img;
				AnimatedIcon = anim;
				img.Realized += HandleRealized;
				img.Unrealized += HandleUnrealized;
				img.Destroyed += HandleDestroyed;
				if (img.IsRealized)
					StartAnimation ();
			}

			void StartAnimation ()
			{
				if (Animation == null) {
					Animation = AnimatedIcon.StartAnimation (delegate (Xwt.Drawing.Image pix) {
						Image.Pixbuf = pix.ToPixbuf ();
					});
				}
			}

			void StopAnimation ()
			{
				if (Animation != null) {
					Animation.Dispose ();
					Animation = null;
				}
			}

			void HandleDestroyed (object sender, EventArgs e)
			{
				UnregisterImageAnimation (this);
			}

			void HandleUnrealized (object sender, EventArgs e)
			{
				StopAnimation ();
			}

			void HandleRealized (object sender, EventArgs e)
			{
				StartAnimation ();
			}

			public void Dispose ()
			{
				StopAnimation ();
				Image.Realized -= HandleRealized;
				Image.Unrealized -= HandleUnrealized;
				Image.Destroyed -= HandleDestroyed;
			}
		}

		public static void LoadIcon (this Gtk.Image image, string iconId, Gtk.IconSize size)
		{
			AnimatedImageInfo ainfo = animatedImages.Select (a => (AnimatedImageInfo) a.Target).FirstOrDefault (a => a != null && a.Image == image);
			if (ainfo != null) {
				if (ainfo.AnimatedIcon.AnimationSpec == iconId)
					return;
				UnregisterImageAnimation (ainfo);
			}
			if (IsAnimation (iconId, size)) {
				var anim = GetAnimatedIcon (iconId);
				ainfo = new AnimatedImageInfo (image, anim);
				ainfo.Animation = anim.StartAnimation (delegate (Xwt.Drawing.Image pix) {
					image.Pixbuf = pix.ToPixbuf ();
				});
				animatedImages.Add (new WeakReference (ainfo));
			} else
				image.SetFromStock (iconId, size);
		}

		static void UnregisterImageAnimation (AnimatedImageInfo ainfo)
		{
			ainfo.Dispose ();
			animatedImages.RemoveAll (a => (AnimatedImageInfo)a.Target == ainfo);
		}

		//TODO: size-limit the in-memory cache
		//TODO: size-limit the on-disk cache
		static Dictionary<string,ImageLoader> gravatars = new Dictionary<string,ImageLoader> ();

		public static ImageLoader GetUserIcon (string email, int size, Xwt.Screen screen = null)
		{

			if (screen == null) {
				screen = Xwt.Desktop.PrimaryScreen;
			}

			//only support integer scaling for now
			var scaleFactor = (int) screen.ScaleFactor;
			size = size * scaleFactor;

			var hash = GetMD5Hash (email);
			string key = hash + "@" + size + "x" + size;

			if (scaleFactor != 1) {
				key += "x" + scaleFactor;
			}

			ImageLoader loader;
			if (!gravatars.TryGetValue (key, out loader) || (!loader.Downloading && loader.Image == null)) {
				var cacheFile = UserProfile.Current.TempDir.Combine ("Gravatars", key);
				string url = "https://www.gravatar.com/avatar/" + hash + "?d=404&s=" + size;
				gravatars[key] = loader = new ImageLoader (cacheFile, url, scaleFactor);
			}

			return loader;
		}

		static string GetMD5Hash (string email)
		{
			var md5 = System.Security.Cryptography.MD5.Create ();
			byte[] hash = md5.ComputeHash (Encoding.UTF8.GetBytes (email.Trim ().ToLower ()));
			StringBuilder sb = new StringBuilder ();
			foreach (byte b in hash)
				sb.Append (b.ToString ("x2"));
			return sb.ToString ();
		}

		public static void LoadUserIcon (this Gtk.Image image, string email, int size)
		{
			image.WidthRequest = size;
			image.HeightRequest = size;
			ImageLoader gravatar = GetUserIcon (email, size);
			gravatar.Completed += delegate {
				if (gravatar.Image != null)
					image.Pixbuf = gravatar.Image.ToPixbuf ();
			};
		}
	}

}
