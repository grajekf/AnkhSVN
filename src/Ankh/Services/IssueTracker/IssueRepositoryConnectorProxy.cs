﻿using System;
using System.Collections.Generic;
using System.Text;

using Ankh.ExtensionPoints.IssueTracker;

namespace Ankh.Services.IssueTracker
{
    /// <summary>
    /// Acts as a proxy to the the actual Issue Tracker Repository Connector.
    /// </summary>
    /// <remarks>
    /// This proxy serves "descriptive" properties w/o initializing the actual connector.
    /// The actual connector package initialization is delayed until a non-descriptive property is needed.
    /// Currently, "connector name" is the only descriptive property.
    /// </remarks>
    class IssueRepositoryConnectorProxy : IssueRepositoryConnector
    {
        private IssueRepositoryConnector _delegate = null;
        private IAnkhServiceProvider _context;
        private string _name = null;
        private string _delegateId = null;

        public IssueRepositoryConnectorProxy(IAnkhServiceProvider context, string name, string delegateServiceId)
        {
            _context = context;
            _name = name;
            _delegateId = delegateServiceId;
        }

        private IssueRepositoryConnector Delegate
        {
            get
            {
                if (_delegate == null
                    && !string.IsNullOrEmpty(_delegateId))
                {
                    Type serviceType = Type.GetTypeFromCLSID(new Guid(_delegateId));
                    if (serviceType != null)
                    {
                        _delegate = _context.GetService<IssueRepositoryConnector>(serviceType);
                    }
                }
                return _delegate;
            }
        }

        #region IIssueRepositoryConnector Members

        public override IssueRepository Create(IssueRepositorySettings settings)
        {
            IssueRepositoryConnector dlg = Delegate;
            if (dlg != null)
            {
                return dlg.Create(settings);
            }
            return null;
        }

        public override IssueRepositoryConfigurationPage ConfigurationPage
        {
            get
            {
                IssueRepositoryConnector dlg = Delegate;
                if (dlg != null)
                {
                    return dlg.ConfigurationPage;
                }
                return null;
            }
        }

        public override string Name
        {
            get { return _name; }
        }

        #endregion

    }
}
