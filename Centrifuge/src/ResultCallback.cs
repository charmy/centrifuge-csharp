using System;

namespace Centrifuge
{
    public delegate void ResultCallback<T>(Exception e, T result);
}