using System;

namespace Centrifuge
{
    /* Call this with exception or token. If called with null error and empty token then client considers access unauthorized */
    public delegate void TokenCallback(Exception e, string token);
}