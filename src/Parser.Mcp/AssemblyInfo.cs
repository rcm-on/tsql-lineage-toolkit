using System.Runtime.CompilerServices;

// HandleLine es la unidad probable del transporte (una petición JSON-RPC dentro, una
// respuesta fuera) sin levantar proceso; queda internal para no fijarla como API pública.
[assembly: InternalsVisibleTo("TSqlParser.Tests")]
