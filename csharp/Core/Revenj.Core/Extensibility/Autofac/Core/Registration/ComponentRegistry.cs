﻿// This software is part of the Autofac IoC container
// Copyright © 2011 Autofac Contributors
// http://autofac.org
//
// Permission is hereby granted, free of charge, to any person
// obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use,
// copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following
// conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Revenj.Extensibility.Autofac.Util;

namespace Revenj.Extensibility.Autofac.Core.Registration
{
	/// <summary>
	/// Maps services onto the components that provide them.
	/// </summary>
	/// <remarks>
	/// The component registry provides services directly from components,
	/// and also uses <see cref="IRegistrationSource"/> to generate components
	/// on-the-fly or as adapters for other components. A component registry
	/// is normally used through a <see cref="ContainerBuilder"/>, and not
	/// directly by application code.
	/// </remarks>
	public class ComponentRegistry : Disposable, IComponentRegistry
	{
		/// <summary>
		/// Protects instance variables from concurrent access.
		/// </summary>
		readonly object _synchRoot = new object();

		/// <summary>
		/// External registration sources.
		/// </summary>
		readonly LinkedList<IRegistrationSource> _dynamicRegistrationSources = new LinkedList<IRegistrationSource>();
		IRegistrationSource[] _dynamicRegistrationSourcesArray;

		/// <summary>
		/// All registrations.
		/// </summary>
		readonly List<IComponentRegistration> _registrations = new List<IComponentRegistration>();
		IComponentRegistration[] _registrationsCachedArray;

		/// <summary>
		/// Keeps track of the status of registered services.
		/// TODO: this should be populated on build
		/// </summary>
		readonly Dictionary<Service, ServiceRegistrationInfo> _serviceInfo = new Dictionary<Service, ServiceRegistrationInfo>();

		/// <summary>
		/// Releases unmanaged and - optionally - managed resources
		/// </summary>
		/// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
		protected override void Dispose(bool disposing)
		{
			_registrationsCachedArray = null;
			_dynamicRegistrationSourcesArray = null;
			foreach (var registration in _registrations)
				registration.Dispose();

			base.Dispose(disposing);
		}

		/// <summary>
		/// Attempts to find a default registration for the specified service.
		/// </summary>
		/// <param name="service">The service to look up.</param>
		/// <param name="registration">The default registration for the service.</param>
		/// <returns>True if a registration exists.</returns>
		public bool TryGetRegistration(Service service, out IComponentRegistration registration)
		{
			if (service == null) throw new ArgumentNullException("service");
			lock (_synchRoot)
			{
				var info = GetInitializedServiceInfo(service);
				return info.TryGetRegistration(out registration);
			}
		}

		/// <summary>
		/// Determines whether the specified service is registered.
		/// </summary>
		/// <param name="service">The service to test.</param>
		/// <returns>True if the service is registered.</returns>
		public bool IsRegistered(Service service)
		{
			if (service == null) throw new ArgumentNullException("service");
			lock (_synchRoot)
			{
				return GetInitializedServiceInfo(service).IsRegistered;
			}
		}

		/// <summary>
		/// Register a component.
		/// </summary>
		/// <param name="registration">The component registration.</param>
		public void Register(IComponentRegistration registration)
		{
			Register(registration, false);
		}

		/// <summary>
		/// Register a component.
		/// </summary>
		/// <param name="registration">The component registration.</param>
		/// <param name="preserveDefaults">If true, existing defaults for the services provided by the
		/// component will not be changed.</param>
		public virtual void Register(IComponentRegistration registration, bool preserveDefaults)
		{
			if (registration == null) throw new ArgumentNullException("registration");

			lock (_synchRoot)
			{
				AddRegistration(registration, preserveDefaults);
				UpdateInitialisedAdapters(registration);
			}
		}

		void UpdateInitialisedAdapters(IComponentRegistration registration)
		{
			var adapterServices = new List<Service>(_serviceInfo.Count);
			foreach (var kv in _serviceInfo)
				if (kv.Value.ShouldRecalculateAdaptersOn(registration))
					adapterServices.Add(kv.Key);

			if (adapterServices.Count == 0)
				return;

			Debug.WriteLine(String.Format(
				"[Autofac] Component '{0}' provides services that have already been adapted. Consider refactoring to ContainerBuilder.Build() rather than Update().",
				registration));

			var drs = _dynamicRegistrationSourcesArray;
			if (drs == null)
			{
				drs = new IRegistrationSource[_dynamicRegistrationSources.Count];
				_dynamicRegistrationSources.CopyTo(drs, 0);
				_dynamicRegistrationSourcesArray = drs;
			}

			var adaptationSandbox = new AdaptationSandbox(
				drs.Where(rs => rs.IsAdapterForIndividualComponents),
				registration,
				adapterServices);

			var adapters = adaptationSandbox.GetAdapters();
			foreach (var adapter in adapters)
				AddRegistration(adapter, true);
		}

		void AddRegistration(IComponentRegistration registration, bool preserveDefaults)
		{
			foreach (var service in registration.Services)
			{
				var info = GetServiceInfo(service);
				info.AddImplementation(registration, preserveDefaults);
			}

			_registrations.Add(registration);
			_registrationsCachedArray = null;

			var r = Registered;
			if (r != null)
				r(this, new ComponentRegisteredEventArgs(this, registration));
		}

		/// <summary>
		/// Enumerate the registered components.
		/// </summary>
		public IEnumerable<IComponentRegistration> Registrations
		{
			get
			{
				var rca = _registrationsCachedArray;
				if (rca == null)
				{
					lock (_synchRoot)
						_registrationsCachedArray = rca = _registrations.ToArray();
				}
				return rca;
			}
		}

		/// <summary>
		/// Selects from the available registrations after ensuring that any
		/// dynamic registration sources that may provide <paramref name="service"/>
		/// have been invoked.
		/// </summary>
		/// <param name="service">The service for which registrations are sought.</param>
		/// <returns>Registrations supporting <paramref name="service"/>.</returns>
		public IEnumerable<IComponentRegistration> RegistrationsFor(Service service)
		{
			if (service == null) throw new ArgumentNullException("service");
			lock (_synchRoot)
			{
				var info = GetInitializedServiceInfo(service);
				return info.Implementations;
			}
		}

		/// <summary>
		/// Fired whenever a component is registered - either explicitly or via a
		/// <see cref="IRegistrationSource"/>.
		/// </summary>
		public event EventHandler<ComponentRegisteredEventArgs> Registered;

		/// <summary>
		/// Add a registration source that will provide registrations on-the-fly.
		/// </summary>
		/// <param name="source">The source to register.</param>
		public void AddRegistrationSource(IRegistrationSource source)
		{
			//if (source == null) throw new ArgumentNullException("source");
			lock (_synchRoot)
			{
				_dynamicRegistrationSources.AddFirst(source);
				_dynamicRegistrationSourcesArray = null;
				foreach (var siv in _serviceInfo.Values)
					siv.Include(source);

				var rsa = RegistrationSourceAdded;
				if (rsa != null)
					rsa(this, new RegistrationSourceAddedEventArgs(this, source));
			}
		}

		/// <summary>
		/// Gets the registration sources that are used by the registry.
		/// </summary>
		public IEnumerable<IRegistrationSource> Sources
		{
			get
			{
				var drs = _dynamicRegistrationSourcesArray;
				if (drs == null)
				{
					lock (_synchRoot)
					{
						drs = new IRegistrationSource[_dynamicRegistrationSources.Count];
						_dynamicRegistrationSources.CopyTo(drs, 0);
						_dynamicRegistrationSourcesArray = drs;// = _dynamicRegistrationSources.ToArray();
					}
				}
				return drs;
			}
		}

		/// <summary>
		/// True if the registry contains its own components; false if it is forwarding
		/// registrations from another external registry.
		/// </summary>
		/// <remarks>This property is used when walking up the scope tree looking for
		/// registrations for a new customised scope. (See issue 336.)</remarks>
		public bool HasLocalComponents
		{
			get { return true; }
		}

		/// <summary>
		/// Fired when an <see cref="IRegistrationSource"/> is added to the registry.
		/// </summary>
		public event EventHandler<RegistrationSourceAddedEventArgs> RegistrationSourceAdded;

		ServiceRegistrationInfo GetInitializedServiceInfo(Service service)
		{
			var info = GetServiceInfo(service);
			if (!info.IsInitialized)
				InitializeInfo(service, info);

			return info;
		}

		void InitializeInfo(Service service, ServiceRegistrationInfo info)
		{
			if (!info.IsInitializing)
				info.BeginInitialization(_dynamicRegistrationSources);

			while (info.HasSourcesToQuery)
			{
				var next = info.DequeueNextSource();
				foreach (var provided in next.RegistrationsFor(service, RegistrationsFor))
				{
					// This ensures that multiple services provided by the same
					// component share a single component (we don't re-query for them)
					foreach (var additionalService in provided.Services)
					{
						var additionalInfo = GetServiceInfo(additionalService);
						if (additionalInfo.IsInitialized) continue;

						if (!additionalInfo.IsInitializing)
							additionalInfo.BeginInitialization(_dynamicRegistrationSources.Where(src => src != next));
						else
							additionalInfo.SkipSource(next);
					}

					AddRegistration(provided, true);
				}
			}

			info.CompleteInitialization();
		}

		ServiceRegistrationInfo GetServiceInfo(Service service)
		{
			ServiceRegistrationInfo existing;
			if (_serviceInfo.TryGetValue(service, out existing))
				return existing;

			var info = new ServiceRegistrationInfo(service);
			_serviceInfo.Add(service, info);
			return info;
		}
	}
}
