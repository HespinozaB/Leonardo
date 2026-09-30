"""Servidor MCP para Revit 2024.

Claude (Desktop o Code) lanza este proceso por stdio. Cada herramienta reenvía la
petición al add-in RevitMCP, que escucha en 127.0.0.1:<REVIT_MCP_PORT> (8765 por
defecto) dentro de Revit, y devuelve la respuesta.

Todas las longitudes y coordenadas se expresan en milímetros.
"""

from __future__ import annotations

import base64
import itertools
import json
import os
import socket
from typing import Any

from mcp.server.fastmcp import FastMCP, Image

HOST = os.environ.get("REVIT_MCP_HOST", "127.0.0.1")
PORT = int(os.environ.get("REVIT_MCP_PORT", "8765"))
TIMEOUT = float(os.environ.get("REVIT_MCP_TIMEOUT", "320"))

mcp = FastMCP(
    "revit",
    instructions=(
        "Controla Revit 2024 abierto en este ordenador. Unidades: milímetros. "
        "Revit puede estar en español: las categorías aceptan el nombre interno "
        "(OST_Walls), el inglés (Walls) o el localizado (Muros). Antes de modificar, "
        "consulta (info_proyecto, listar_niveles, listar_tipos) para usar nombres reales. "
        "Usa ejecutar_codigo solo cuando ninguna otra herramienta sirva."
    ),
)

_ids = itertools.count(1)


class RevitError(RuntimeError):
    pass


def call(method: str, **params: Any) -> Any:
    """Envía una petición JSON (una línea) al add-in y devuelve 'result'."""
    params = {k: v for k, v in params.items() if v is not None}
    request = {"id": next(_ids), "method": method, "params": params}
    try:
        with socket.create_connection((HOST, PORT), timeout=10) as sock:
            sock.settimeout(TIMEOUT)
            sock.sendall((json.dumps(request, ensure_ascii=False) + "\n").encode("utf-8"))
            buffer = bytearray()
            while not buffer.endswith(b"\n"):
                chunk = sock.recv(65536)
                if not chunk:
                    break
                buffer.extend(chunk)
    except ConnectionRefusedError as exc:
        raise RevitError(
            f"No hay conexión con Revit en {HOST}:{PORT}. Abre Revit 2024 con el add-in "
            "RevitMCP instalado y comprueba en la pestaña 'MCP' que el servidor está activo."
        ) from exc
    except socket.timeout as exc:
        raise RevitError("Revit no respondió a tiempo (¿hay un diálogo abierto?).") from exc

    if not buffer:
        raise RevitError("Revit cerró la conexión sin responder.")
    response = json.loads(buffer.decode("utf-8-sig"))
    if not response.get("ok"):
        raise RevitError(response.get("error") or "Error desconocido en Revit.")
    return response.get("result")


def as_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, indent=1)


# ---------------------------------------------------------------- Consulta


@mcp.tool()
def estado() -> str:
    """Comprueba si Revit y el add-in RevitMCP responden."""
    call("ping")
    return as_json(call("info_proyecto"))


@mcp.tool()
def info_proyecto() -> str:
    """Versión e idioma de Revit, documento activo, vista activa y datos del proyecto."""
    return as_json(call("info_proyecto"))


@mcp.tool()
def listar_niveles() -> str:
    """Lista los niveles del proyecto con su elevación en mm."""
    return as_json(call("listar_niveles"))


@mcp.tool()
def listar_vistas(tipo: str | None = None, texto: str | None = None, limite: int = 500) -> str:
    """Lista vistas y hojas.

    tipo: FloorPlan, CeilingPlan, Section, Elevation, ThreeD, DrawingSheet, Schedule...
    texto: filtra por parte del nombre.
    """
    return as_json(call("listar_vistas", tipo=tipo, texto=texto, limite=limite))


@mcp.tool()
def listar_categorias(con_conteo: bool = True, incluir_vacias: bool = False) -> str:
    """Categorías del proyecto (nombre localizado + nombre interno OST_*) y nº de elementos."""
    return as_json(call("listar_categorias", con_conteo=con_conteo, incluir_vacias=incluir_vacias))


@mcp.tool()
def listar_tipos(categoria: str | None = None, texto: str | None = None, limite: int = 300) -> str:
    """Tipos de familia/sistema disponibles (id, familia, tipo). Ej.: categoria='Muros' o 'OST_Doors'."""
    return as_json(call("listar_tipos", categoria=categoria, texto=texto, limite=limite))


@mcp.tool()
def consultar_elementos(
    categoria: str | None = None,
    nivel: str | None = None,
    texto: str | None = None,
    parametro: str | None = None,
    valor: str | None = None,
    solo_vista_activa: bool = False,
    limite: int = 100,
) -> str:
    """Busca elementos del modelo.

    categoria: 'Muros', 'Walls' u 'OST_Walls'. nivel: nombre o id.
    texto: parte del nombre del elemento. parametro/valor: filtra por valor de parámetro (contiene).
    """
    return as_json(
        call(
            "consultar_elementos",
            categoria=categoria,
            nivel=nivel,
            texto=texto,
            parametro=parametro,
            valor=valor,
            solo_vista_activa=solo_vista_activa,
            limite=limite,
        )
    )


@mcp.tool()
def obtener_parametros(ids: list[int], filtro: str | None = None, incluir_tipo: bool = False) -> str:
    """Parámetros (nombre, valor con unidades, solo lectura) de uno o varios elementos."""
    return as_json(call("obtener_parametros", ids=ids, filtro=filtro, incluir_tipo=incluir_tipo))


@mcp.tool()
def obtener_seleccion(limite: int = 200) -> str:
    """Elementos seleccionados actualmente por el usuario en Revit."""
    return as_json(call("obtener_seleccion", limite=limite))


# ---------------------------------------------------------------- Interfaz


@mcp.tool()
def seleccionar_elementos(ids: list[int], enfocar: bool = True) -> str:
    """Selecciona elementos en Revit y (opcional) hace zoom a ellos."""
    return as_json(call("seleccionar_elementos", ids=ids, enfocar=enfocar))


@mcp.tool()
def abrir_vista(vista: str) -> str:
    """Activa una vista u hoja por nombre, número de hoja o id."""
    return as_json(call("abrir_vista", vista=vista))


@mcp.tool()
def captura_vista(vista: str | None = None, ancho_px: int = 1600) -> Image:
    """Devuelve una imagen PNG de una vista (por defecto, la activa) para ver el modelo."""
    result = call("captura_vista", vista=vista, ancho_px=ancho_px)
    return Image(data=base64.b64decode(result["base64"]), format="png")


# ---------------------------------------------------------------- Modificación


@mcp.tool()
def establecer_parametro(ids: list[int], parametro: str, valor: str, en_tipo: bool = False) -> str:
    """Cambia un parámetro en uno o varios elementos.

    parametro: nombre visible (p. ej. 'Comentarios', 'Marca') o BuiltInParameter (ALL_MODEL_INSTANCE_COMMENTS).
    valor: texto; para longitudes usa las unidades del proyecto ('3000' o '3000 mm').
    en_tipo: True para modificar el tipo del elemento en lugar de la instancia.
    """
    return as_json(call("establecer_parametro", ids=ids, parametro=parametro, valor=valor, en_tipo=en_tipo))


@mcp.tool()
def crear_nivel(elevacion_mm: float, nombre: str | None = None) -> str:
    """Crea un nivel a la elevación indicada (mm)."""
    return as_json(call("crear_nivel", elevacion_mm=elevacion_mm, nombre=nombre))


@mcp.tool()
def crear_muro(
    x1: float,
    y1: float,
    x2: float,
    y2: float,
    nivel: str | None = None,
    tipo: str | None = None,
    altura_mm: float = 3000,
    desfase_base_mm: float = 0,
    estructural: bool = False,
) -> str:
    """Crea un muro recto de (x1,y1) a (x2,y2) en mm. tipo: nombre o id de tipo de muro."""
    return as_json(
        call(
            "crear_muro",
            x1=x1,
            y1=y1,
            x2=x2,
            y2=y2,
            nivel=nivel,
            tipo=tipo,
            altura_mm=altura_mm,
            desfase_base_mm=desfase_base_mm,
            estructural=estructural,
        )
    )


@mcp.tool()
def crear_suelo(puntos: list[list[float]], nivel: str | None = None, tipo: str | None = None) -> str:
    """Crea un suelo con el contorno cerrado dado: [[x,y], [x,y], ...] en mm (mín. 3 puntos)."""
    return as_json(call("crear_suelo", puntos=puntos, nivel=nivel, tipo=tipo))


@mcp.tool()
def colocar_familia(
    tipo: str,
    x: float,
    y: float,
    z: float = 0,
    nivel: str | None = None,
    rotacion_grados: float = 0,
    anfitrion_id: int | None = None,
) -> str:
    """Coloca un ejemplar de familia (mobiliario, puerta, ventana...) en (x,y,z) mm.

    tipo: 'Familia : Tipo', nombre del tipo o id. Para puertas/ventanas indica anfitrion_id (el muro).
    """
    return as_json(
        call(
            "colocar_familia",
            tipo=tipo,
            x=x,
            y=y,
            z=z,
            nivel=nivel,
            rotacion_grados=rotacion_grados,
            anfitrion_id=anfitrion_id,
        )
    )


@mcp.tool()
def crear_habitacion(x: float, y: float, nivel: str | None = None, nombre: str | None = None, numero: str | None = None) -> str:
    """Crea una habitación en el recinto cerrado que contiene el punto (x,y) mm."""
    return as_json(call("crear_habitacion", x=x, y=y, nivel=nivel, nombre=nombre, numero=numero))


@mcp.tool()
def crear_vista_planta(nivel: str, nombre: str | None = None, techo: bool = False) -> str:
    """Crea una vista de planta (o de techo si techo=True) para un nivel."""
    return as_json(call("crear_vista_planta", nivel=nivel, nombre=nombre, techo=techo))


@mcp.tool()
def mover_elementos(ids: list[int], dx: float = 0, dy: float = 0, dz: float = 0) -> str:
    """Desplaza elementos (mm)."""
    return as_json(call("mover_elementos", ids=ids, dx=dx, dy=dy, dz=dz))


@mcp.tool()
def copiar_elementos(ids: list[int], dx: float = 0, dy: float = 0, dz: float = 0) -> str:
    """Copia elementos con un desplazamiento (mm). Devuelve los ids nuevos."""
    return as_json(call("copiar_elementos", ids=ids, dx=dx, dy=dy, dz=dz))


@mcp.tool()
def eliminar_elementos(ids: list[int]) -> str:
    """Elimina elementos (y sus dependientes). Se puede deshacer con Ctrl+Z en Revit."""
    return as_json(call("eliminar_elementos", ids=ids))


# ---------------------------------------------------------------- Avanzado


@mcp.tool()
def ejecutar_codigo(codigo: str, transaccion: bool = True) -> str:
    """Ejecuta C# (sintaxis C# 5: sin $"..." ni ?.) contra la API de Revit 2024.

    El código es el cuerpo de: object Run(UIApplication uiapp, UIDocument uidoc, Document doc).
    Usa 'return' para devolver datos. Unidades internas de la API: pies (1 pie = 304.8 mm).
    Con transaccion=True se ejecuta dentro de una transacción (no abras otra).
    Ejemplo: return new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount();
    """
    return as_json(call("ejecutar_codigo", codigo=codigo, transaccion=transaccion))


def main() -> None:
    mcp.run()


if __name__ == "__main__":
    main()
