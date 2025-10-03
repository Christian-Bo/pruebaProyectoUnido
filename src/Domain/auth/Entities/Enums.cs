namespace Auth.Domain.Entities; // ubicacion de la carpeta
public enum MetodoLogin { password, facial, qr } // dependiendo que login escoja, por ello los metodos
public enum TipoNotificacion { email, whatsapp } // metodo para envio de info

/*Nota este archivo sirve para metodo de inicio de sesion / envio de notificación el cual el usuario puede escoger
y dependiendo de lo que escoja sera el archivo llamado*/
