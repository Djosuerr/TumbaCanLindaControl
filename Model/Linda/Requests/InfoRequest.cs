using System;

namespace Tumba.CanLindaControl.Model.Linda.Requests
{
    public class InfoRequest : BaseRequest
    {
        public override string Method { get { return "getinfo"; } }
        public override object[] MethodParams { get { return new string[0]; } }
    }
}