using System;
using System.Collections.Generic;
using System.Linq;
using MonoDevelop.Core;
using MonoDevelop.Ide;
using MonoDevelop.Ide.Gui;
using MonoDevelop.Ide.Gui.Components;
using MonoDevelop.Projects;
using Xwt;

namespace MonoDevelop.ConnectedServices.Gui.ServicesTab
{
	class ServiceWidget : FrameBox
	{
		HBox addedWidget;
		ImageView image;
		Label title, description, platforms;
		Button addButton;
		AnimatedIcon animatedStatusIcon;
		IDisposable statusIconAnimation;
		bool showDetails = false;

		IConnectedService service;

		public IConnectedService Service {
			get {
				return service;
			}
			set {
				if (service == value)
					return;
				if (value == null)
					throw new InvalidOperationException ("Service can not be null");
				if (service != null) {
					service.Added -= HandleServiceAddedRemoved;
					service.Adding -= HandleServiceAdding;
					service.AddingFailed -= HandleServiceAddingFailed;
					service.Removed -= HandleServiceAddedRemoved;
				}
				
				service = value;
				image.Image = (service.GalleryIcon ?? ImageService.GetIcon ("md-service")).WithSize (IconSize.Medium);
				title.Markup = "<b>" + service.DisplayName + "</b>";
				description.Text = service.Description;

				platforms.Text = service.SupportedPlatforms;

				addedWidget.Visible = service.IsAdded && !showDetails;

				addButton.Visible = showDetails;
				addButton.Sensitive = !service.IsAdded;
				addButton.Image = service.IsAdded ? ImageService.GetIcon ("md-checkmark").WithSize (IconSize.Small).WithAlpha (0.4) : null;
				addButton.Label = service.IsAdded ? GettextCatalog.GetString ("Enabled") : GettextCatalog.GetString ("Enable");

				service.Added += HandleServiceAddedRemoved;
				service.Adding += HandleServiceAdding;
				service.AddingFailed += HandleServiceAddingFailed;
				service.Removed += HandleServiceAddedRemoved;
			}
		}

		public bool ShowDetails {
			get {
				return showDetails;
			}
			set {
				showDetails = value;
				platforms.Visible = showDetails && !string.IsNullOrEmpty (service?.SupportedPlatforms);
				addButton.Visible = showDetails;
				addedWidget.Visible = service?.IsAdded == true && !showDetails;
			}
		}

		public ServiceWidget (IConnectedService service, bool showDetails = false) : this (showDetails)
		{
			if (service == null)
				throw new ArgumentNullException (nameof (service));
			Service = service;
		}

		public ServiceWidget (bool showDetails = false)
		{
			BackgroundColor = Styles.BaseBackgroundColor;
			BorderColor = Styles.ThinSplitterColor;
			BorderWidth = 1;

			image = new ImageView ();
			title = new Label ();
			title.Font = title.Font.WithSize (16);

			addedWidget = new HBox ();
			addedWidget.Spacing = 3;
			addedWidget.PackStart (new ImageView (ImageService.GetIcon ("md-checkmark").WithSize (IconSize.Small)));
			addedWidget.PackStart (new Label (GettextCatalog.GetString ("Enabled")) {
				Font = Font.WithSize (12),
				TextColor = Styles.SecondaryTextColor,
			});
			addedWidget.Visible = false;

			addButton = new Button (GettextCatalog.GetString ("Enable"));
			addButton.Visible = false;
			addButton.Clicked += HandleAddButtonClicked;

			if (ImageService.IsAnimation ("md-spinner-16", Gtk.IconSize.Menu))
				animatedStatusIcon = ImageService.GetAnimatedIcon ("md-spinner-16", Gtk.IconSize.Menu);

			var header = new HBox ();
			header.Spacing = 10;
			header.PackStart (image);
			header.PackStart (title);
			header.PackStart (addedWidget);

			var vbox = new VBox ();
			vbox.Spacing = 10;
			vbox.PackStart (header);

			description = new Label ();
			description.TextColor = Styles.SecondaryTextColor;
			description.Wrap = WrapMode.Word;
			description.Ellipsize = EllipsizeMode.None;

			platforms = new Label ();
			platforms.TextColor = Styles.SecondaryTextColor;

			vbox.PackStart (description);
			vbox.PackStart (platforms);

			var container = new HBox { Spacing = 0 };
			container.Margin = 30;
			container.PackStart (vbox, true);
			container.PackEnd (addButton, vpos: WidgetPlacement.Start);

			Content = container;
			ShowDetails = showDetails;
		}

		void HandleAddButtonClicked (object sender, EventArgs e)
		{
			if (!service.IsAdded) {

				var addProjects = new Dictionary<string, DotNetProject> ();

				foreach (DotNetProject project in service.Project.ParentSolution.GetAllProjects ().Where (p => p is DotNetProject && p != service.Project)) {
					var svc = project.GetConnectedServicesBinding ()?.SupportedServices.FirstOrDefault (s => s.Id == service.Id);
					if (svc != null && !svc.IsAdded)
						addProjects [project.ItemId] = project;
				}

				if (addProjects.Count > 0) {
					var question = new Xwt.QuestionMessage (GettextCatalog.GetString ("Add {0} to {1}", this.Service.DisplayName, this.Service.Project.Name));
					question.SecondaryText = GettextCatalog.GetString ("Also add '{0}' to other projects in the solution?", this.Service.DisplayName);

					foreach (var project in addProjects)
						question.AddOption (project.Key, project.Value.Name, true);

					var cmdContinue = new Command (GettextCatalog.GetString ("Continue"));
					var cmdProjectOnly = new Command (GettextCatalog.GetString ("Skip"));
					var cmdCancel = new Command (GettextCatalog.GetString ("Cancel"));
					question.Buttons.Add (cmdContinue);
					question.Buttons.Add (cmdProjectOnly);
					question.Buttons.Add (cmdCancel);
					question.DefaultButton = 0;

					Xwt.Toolkit.NativeEngine.Invoke (delegate {
						var result = MessageDialog.AskQuestion (question);

						if (result != cmdCancel) {
							service.AddToProject ();
							if (result == cmdContinue)
								foreach (var project in addProjects) {
									if (question.GetOptionValue (project.Key)) {
										var svc = project.Value.GetConnectedServicesBinding ()?.SupportedServices.FirstOrDefault (s => s.Id == service.Id);
										svc.AddToProject ();
									}
								}
						}
					});
				} else
					service.AddToProject ();
			}
		}

		void HandleServiceAdding (object sender, EventArgs e)
		{
			// TODO: should adding be visible without details / in the gallery view?
			if (!showDetails)
				return;
			Runtime.RunInMainThread (delegate {
				addButton.Label = GettextCatalog.GetString ("Enabling\u2026");
				if (statusIconAnimation == null) {
					if (animatedStatusIcon != null) {
						addButton.Image = animatedStatusIcon.FirstFrame;
						statusIconAnimation = animatedStatusIcon.StartAnimation (p => {
							addButton.Image = p;
						});
					} else
						addButton.Image = ImageService.GetIcon ("md-spinner-16").WithSize (Xwt.IconSize.Small);
				}
				addButton.Sensitive = false;
			});
		}

		void StopButtonAnimation ()
		{
			if (statusIconAnimation != null) {
				statusIconAnimation.Dispose ();
				statusIconAnimation = null;
			}
		}

		void HandleServiceAddingFailed (object sender, EventArgs e)
		{
			Runtime.RunInMainThread (delegate {
				addedWidget.Visible = false;
				StopButtonAnimation ();
				addButton.Image = ImageService.GetIcon ("md-error").WithSize (IconSize.Small);
				addButton.Label = GettextCatalog.GetString ("Retry");
				addButton.Sensitive = true;
			});
		}

		void HandleServiceAddedRemoved (object sender, EventArgs e)
		{
			Runtime.RunInMainThread (delegate {
				addedWidget.Visible = Service.IsAdded && !showDetails;
				StopButtonAnimation ();
				addButton.Image = service.IsAdded ? ImageService.GetIcon ("md-checkmark").WithSize (IconSize.Small).WithAlpha (0.4) : null;
				addButton.Label = service.IsAdded ? GettextCatalog.GetString ("Enabled") : GettextCatalog.GetString ("Enable");
				addButton.Sensitive = !Service.IsAdded;
			});
		}

		protected override void Dispose (bool disposing)
		{
			if (disposing)
				StopButtonAnimation ();

			if (service != null) {
				service.Added -= HandleServiceAddedRemoved;
				service.Adding -= HandleServiceAdding;
				service.AddingFailed -= HandleServiceAddingFailed;
				service.Removed -= HandleServiceAddedRemoved;
				service = null;
			}
			base.Dispose (disposing);
		}
	}
}
